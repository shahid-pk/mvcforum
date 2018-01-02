﻿namespace MvcForum.Web.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Web.Mvc;
    using System.Web.Security;
    using Application;
    using Application.Akismet;
    using Areas.Admin.ViewModels;
    using Core.Constants;
    using Core.Events;
    using Core.ExtensionMethods;
    using Core.Interfaces;
    using Core.Interfaces.Services;
    using Core.Models.Entities;
    using Core.Models.General;
    using ViewModels.Mapping;
    using ViewModels.Post;
    using MembershipUser = Core.Models.Entities.MembershipUser;

    [Authorize]
    public partial class PostController : BaseController
    {
        private readonly IBannedWordService _bannedWordService;
        private readonly ICategoryService _categoryService;
        private readonly IEmailService _emailService;
        private readonly IPostEditService _postEditService;
        private readonly IPostService _postService;
        private readonly IReportService _reportService;
        private readonly ITopicNotificationService _topicNotificationService;
        private readonly ITopicService _topicService;
        private readonly IVoteService _voteService;
        private readonly IActivityService _activityService;

        public PostController(ILoggingService loggingService, IMembershipService membershipService,
            ILocalizationService localizationService, IRoleService roleService, ITopicService topicService,
            IPostService postService, ISettingsService settingsService, ICategoryService categoryService,
            ITopicNotificationService topicNotificationService, IEmailService emailService,
            IReportService reportService, IBannedWordService bannedWordService, IVoteService voteService,
            IPostEditService postEditService, ICacheService cacheService, IMvcForumContext context, IActivityService activityService)
            : base(loggingService, membershipService, localizationService, roleService,
                settingsService, cacheService, context)
        {
            _topicService = topicService;
            _postService = postService;
            _categoryService = categoryService;
            _topicNotificationService = topicNotificationService;
            _emailService = emailService;
            _reportService = reportService;
            _bannedWordService = bannedWordService;
            _voteService = voteService;
            _postEditService = postEditService;
            _activityService = activityService;
        }


        [HttpPost]
        public ActionResult CreatePost(CreateAjaxPostViewModel post)
        {
            PermissionSet permissions;


            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);

            var loggedOnUser = MembershipService.GetUser(loggedOnReadOnlyUser.Id);

            // Flood control
            if (!_postService.PassedPostFloodTest(loggedOnReadOnlyUser))
            {
                throw new Exception(LocalizationService.GetResourceString("Errors.GenericMessage"));
            }

            // Check stop words
            var stopWords = _bannedWordService.GetAll(true);
            foreach (var stopWord in stopWords)
            {
                if (post.PostContent.IndexOf(stopWord.Word, StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    throw new Exception(LocalizationService.GetResourceString("StopWord.Error"));
                }
            }

            // Quick check to see if user is locked out, when logged in
            if (loggedOnUser.IsLockedOut || !loggedOnUser.IsApproved)
            {
                FormsAuthentication.SignOut();
                throw new Exception(LocalizationService.GetResourceString("Errors.NoAccess"));
            }

            var topic = _topicService.Get(post.Topic);

            var postContent = _bannedWordService.SanitiseBannedWords(post.PostContent);

            var akismetHelper = new AkismetHelper(SettingsService);

            var newPost = _postService.AddNewPost(postContent, topic, loggedOnUser, out permissions);

            // Set the reply to
            newPost.InReplyTo = post.InReplyTo;


            if (akismetHelper.IsSpam(newPost))
            {
                newPost.Pending = true;
            }

            if (!newPost.Pending.HasValue || !newPost.Pending.Value)
            {
                _activityService.PostCreated(newPost);
            }

            try
            {
                Context.SaveChanges();
            }
            catch (Exception ex)
            {
                Context.RollBack();
                LoggingService.Error(ex);
                throw new Exception(LocalizationService.GetResourceString("Errors.GenericMessage"));
            }

            //Check for moderation
            if (newPost.Pending == true)
            {
                return PartialView("_PostModeration");
            }

            // All good send the notifications and send the post back


            // Create the view model
            var viewModel = ViewModelMapping.CreatePostViewModel(newPost, new List<Vote>(), permissions, topic,
                loggedOnReadOnlyUser, SettingsService.GetSettings(), new List<Favourite>());

            // Success send any notifications
            NotifyNewTopics(topic, loggedOnReadOnlyUser);

            // Return view
            return PartialView("_Post", viewModel);
        }

        public ActionResult DeletePost(Guid id)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

            // Got to get a lot of things here as we have to check permissions
            // Get the post
            var post = _postService.Get(id);
            var postId = post.Id;

            // get this so we know where to redirect after
            var isTopicStarter = post.IsTopicStarter;

            // Get the topic
            var topic = post.Topic;
            var topicUrl = topic.NiceUrl;

            // get the users permissions
            var permissions = RoleService.GetPermissions(topic.Category, loggedOnUsersRole);

            if (post.User.Id == loggedOnReadOnlyUser.Id ||
                permissions[SiteConstants.Instance.PermissionDeletePosts].IsTicked)
            {
                // Delete post / topic
                if (post.IsTopicStarter)
                {
                    // Delete entire topic
                    _topicService.Delete(topic);
                }
                else
                {
                    // Deletes single post and associated data
                    _postService.Delete(post, false);

                    // Remove in replyto's
                    var relatedPosts = _postService.GetReplyToPosts(postId);
                    foreach (var relatedPost in relatedPosts)
                    {
                        relatedPost.InReplyTo = null;
                    }
                }

                try
                {
                    Context.SaveChanges();
                }
                catch (Exception ex)
                {
                    Context.RollBack();
                    LoggingService.Error(ex);
                    ShowMessage(new GenericMessageViewModel
                    {
                        Message = LocalizationService.GetResourceString("Errors.GenericMessage"),
                        MessageType = GenericMessages.danger
                    });
                    return Redirect(topicUrl);
                }
            }

            // Deleted successfully
            if (isTopicStarter)
            {
                // Redirect to root as this was a topic and deleted
                TempData[AppConstants.MessageViewBagName] = new GenericMessageViewModel
                {
                    Message = LocalizationService.GetResourceString("Topic.Deleted"),
                    MessageType = GenericMessages.success
                };
                return RedirectToAction("Index", "Home");
            }

            // Show message that post is deleted
            TempData[AppConstants.MessageViewBagName] = new GenericMessageViewModel
            {
                Message = LocalizationService.GetResourceString("Post.Deleted"),
                MessageType = GenericMessages.success
            };

            return Redirect(topic.NiceUrl);
        }

        private ActionResult NoPermission(Topic topic)
        {
            // Trying to be a sneaky mo fo, so tell them
            TempData[AppConstants.MessageViewBagName] = new GenericMessageViewModel
            {
                Message = LocalizationService.GetResourceString("Errors.NoPermission"),
                MessageType = GenericMessages.danger
            };
            return Redirect(topic.NiceUrl);
        }

        private void NotifyNewTopics(Topic topic, MembershipUser loggedOnReadOnlyUser)
        {
            try
            {
                // Get all notifications for this category
                var notifications = _topicNotificationService.GetByTopic(topic).Select(x => x.User.Id).ToList();

                if (notifications.Any())
                {
                    // remove the current user from the notification, don't want to notify yourself that you 
                    // have just made a topic!
                    notifications.Remove(loggedOnReadOnlyUser.Id);

                    if (notifications.Count > 0)
                    {
                        // Now get all the users that need notifying
                        var usersToNotify = MembershipService.GetUsersById(notifications);

                        // Create the email
                        var sb = new StringBuilder();
                        sb.AppendFormat("<p>{0}</p>",
                            string.Format(LocalizationService.GetResourceString("Post.Notification.NewPosts"),
                                topic.Name));
                        if (SiteConstants.Instance.IncludeFullPostInEmailNotifications)
                        {
                            sb.Append(AppHelpers.ConvertPostContent(topic.LastPost.PostContent));
                        }
                        sb.AppendFormat("<p><a href=\"{0}\">{0}</a></p>", string.Concat(Domain, topic.NiceUrl));

                        // create the emails only to people who haven't had notifications disabled
                        var emails = usersToNotify
                            .Where(x => x.DisableEmailNotifications != true && !x.IsLockedOut && x.IsBanned != true)
                            .Select(user => new Email
                            {
                                Body = _emailService.EmailTemplate(user.UserName, sb.ToString()),
                                EmailTo = user.Email,
                                NameTo = user.UserName,
                                Subject = string.Concat(
                                    LocalizationService.GetResourceString("Post.Notification.Subject"),
                                    SettingsService.GetSettings().ForumName)
                            }).ToList();

                        // and now pass the emails in to be sent
                        _emailService.SendMail(emails);

                        Context.SaveChanges();
                    }
                }
            }
            catch (Exception ex)
            {
                Context.RollBack();
                LoggingService.Error(ex);
            }
        }

        public ActionResult Report(Guid id)
        {
            if (SettingsService.GetSettings().EnableSpamReporting)
            {
                var post = _postService.Get(id);
                return View(new ReportPostViewModel {PostId = post.Id, PostCreatorUsername = post.User.UserName});
            }
            return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
        }

        [HttpPost]
        public ActionResult Report(ReportPostViewModel viewModel)
        {
            if (SettingsService.GetSettings().EnableSpamReporting)
            {
                var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);

                var post = _postService.Get(viewModel.PostId);
                var report = new Report
                {
                    Reason = viewModel.Reason,
                    ReportedPost = post,
                    Reporter = loggedOnReadOnlyUser
                };
                _reportService.PostReport(report);

                try
                {
                    Context.SaveChanges();
                }
                catch (Exception ex)
                {
                    Context.RollBack();
                    LoggingService.Error(ex);
                }

                TempData[AppConstants.MessageViewBagName] = new GenericMessageViewModel
                {
                    Message = LocalizationService.GetResourceString("Report.ReportSent"),
                    MessageType = GenericMessages.success
                };
                return View(new ReportPostViewModel {PostId = post.Id, PostCreatorUsername = post.User.UserName});
            }
            return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
        }


        [HttpPost]
        [AllowAnonymous]
        public ActionResult GetAllPostLikes(Guid id)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);
            var post = _postService.Get(id);
            var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
            var votes = _voteService.GetVotesByPosts(new List<Guid> {id});
            var viewModel = ViewModelMapping.CreatePostViewModel(post, votes, permissions, post.Topic,
                loggedOnReadOnlyUser, SettingsService.GetSettings(), new List<Favourite>());
            var upVotes = viewModel.Votes.Where(x => x.Amount > 0).ToList();
            return View(upVotes);
        }


        public ActionResult MovePost(Guid id)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

            // Firstly check if this is a post and they are allowed to move it
            var post = _postService.Get(id);
            if (post == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
            }

            var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
            var allowedCategories = _categoryService.GetAllowedCategories(loggedOnUsersRole);

            // Does the user have permission to this posts category
            var cat = allowedCategories.FirstOrDefault(x => x.Id == post.Topic.Category.Id);
            if (cat == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoPermission"));
            }

            // Does this user have permission to move
            if (!permissions[SiteConstants.Instance.PermissionEditPosts].IsTicked)
            {
                return NoPermission(post.Topic);
            }

            var topics = _topicService.GetAllSelectList(allowedCategories, 30);
            topics.Insert(0, new SelectListItem
            {
                Text = LocalizationService.GetResourceString("Topic.Choose"),
                Value = ""
            });

            var postViewModel = ViewModelMapping.CreatePostViewModel(post, post.Votes.ToList(), permissions, post.Topic,
                loggedOnReadOnlyUser, SettingsService.GetSettings(), post.Favourites.ToList());
            postViewModel.MinimalPost = true;
            var viewModel = new MovePostViewModel
            {
                Post = postViewModel,
                PostId = post.Id,
                LatestTopics = topics,
                MoveReplyToPosts = true
            };
            return View(viewModel);
        }

        [HttpPost]
        public ActionResult MovePost(MovePostViewModel viewModel)
        {
            var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
            var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

            // Firstly check if this is a post and they are allowed to move it
            var post = _postService.Get(viewModel.PostId);
            if (post == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
            }

            var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
            var allowedCategories = _categoryService.GetAllowedCategories(loggedOnUsersRole);

            // Does the user have permission to this posts category
            var cat = allowedCategories.FirstOrDefault(x => x.Id == post.Topic.Category.Id);
            if (cat == null)
            {
                return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoPermission"));
            }

            // Does this user have permission to move
            if (!permissions[SiteConstants.Instance.PermissionEditPosts].IsTicked)
            {
                return NoPermission(post.Topic);
            }

            var previousTopic = post.Topic;
            var category = post.Topic.Category;
            var postCreator = post.User;

            Topic topic;
            var cancelledByEvent = false;
            // If the dropdown has a value, then we choose that first
            if (viewModel.TopicId != null)
            {
                // Get the selected topic
                topic = _topicService.Get((Guid) viewModel.TopicId);
            }
            else if (!string.IsNullOrWhiteSpace(viewModel.TopicTitle))
            {
                // We get the banned words here and pass them in, so its just one call
                // instead of calling it several times and each call getting all the words back
                var bannedWordsList = _bannedWordService.GetAll();
                List<string> bannedWords = null;
                if (bannedWordsList.Any())
                {
                    bannedWords = bannedWordsList.Select(x => x.Word).ToList();
                }

                // Create the topic
                topic = new Topic
                {
                    Name = _bannedWordService.SanitiseBannedWords(viewModel.TopicTitle, bannedWords),
                    Category = category,
                    User = postCreator
                };

                // Create the topic
                topic = _topicService.Add(topic);

                // Save the changes
                Context.SaveChanges();

                // Set the post to be a topic starter
                post.IsTopicStarter = true;

                // Check the Events
                var e = new TopicMadeEventArgs {Topic = topic};
                EventManager.Instance.FireBeforeTopicMade(this, e);
                if (e.Cancel)
                {
                    cancelledByEvent = true;
                    ShowMessage(new GenericMessageViewModel
                    {
                        MessageType = GenericMessages.warning,
                        Message = LocalizationService.GetResourceString("Errors.GenericMessage")
                    });
                }
            }
            else
            {
                // No selected topic OR topic title, just redirect back to the topic
                return Redirect(post.Topic.NiceUrl);
            }

            // If this create was cancelled by an event then don't continue
            if (!cancelledByEvent)
            {
                // Now update the post to the new topic
                post.Topic = topic;

                // Also move any posts, which were in reply to this post
                if (viewModel.MoveReplyToPosts)
                {
                    var relatedPosts = _postService.GetReplyToPosts(viewModel.PostId);
                    foreach (var relatedPost in relatedPosts)
                    {
                        relatedPost.Topic = topic;
                    }
                }

                Context.SaveChanges();

                // Update Last post..  As we have done a save, we should get all posts including the added ones
                var lastPost = topic.Posts.OrderByDescending(x => x.DateCreated).FirstOrDefault();
                topic.LastPost = lastPost;

                // If any of the posts we are moving, were the last post - We need to update the old Topic
                var previousTopicLastPost =
                    previousTopic.Posts.OrderByDescending(x => x.DateCreated).FirstOrDefault();
                previousTopic.LastPost = previousTopicLastPost;

                try
                {
                    Context.SaveChanges();

                    EventManager.Instance.FireAfterTopicMade(this, new TopicMadeEventArgs {Topic = topic});

                    // On Update redirect to the topic
                    return RedirectToAction("Show", "Topic", new {slug = topic.Slug});
                }
                catch (Exception ex)
                {
                    Context.RollBack();
                    LoggingService.Error(ex);
                    ShowMessage(new GenericMessageViewModel
                    {
                        Message = ex.Message,
                        MessageType = GenericMessages.danger
                    });
                }
            }

            // Repopulate the topics
            var topics = _topicService.GetAllSelectList(allowedCategories, 30);
            topics.Insert(0, new SelectListItem
            {
                Text = LocalizationService.GetResourceString("Topic.Choose"),
                Value = ""
            });

            viewModel.LatestTopics = topics;
            viewModel.Post = ViewModelMapping.CreatePostViewModel(post, post.Votes.ToList(), permissions,
                post.Topic, loggedOnReadOnlyUser, SettingsService.GetSettings(), post.Favourites.ToList());
            viewModel.Post.MinimalPost = true;
            viewModel.PostId = post.Id;

            return View(viewModel);
        }

        public ActionResult GetPostEditHistory(Guid id)
        {
            var post = _postService.Get(id);
            if (post != null)
            {
                var loggedOnReadOnlyUser = User.GetMembershipUser(MembershipService);
                var loggedOnUsersRole = loggedOnReadOnlyUser.GetRole(RoleService);

                // Check permissions
                var permissions = RoleService.GetPermissions(post.Topic.Category, loggedOnUsersRole);
                if (permissions[SiteConstants.Instance.PermissionEditPosts].IsTicked)
                {
                    // Good to go
                    var postEdits = _postEditService.GetByPost(id);
                    var viewModel = new PostEditHistoryViewModel
                    {
                        PostEdits = postEdits
                    };
                    return PartialView(viewModel);
                }
            }

            return Content(LocalizationService.GetResourceString("Errors.GenericMessage"));
        }
    }
}