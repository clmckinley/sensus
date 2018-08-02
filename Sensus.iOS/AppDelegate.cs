// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.IO;
using Xamarin;
using Xamarin.Forms;
using Xamarin.Forms.Platform.iOS;
using Xam.Plugin.MapExtend.iOSUnified;
using Sensus.UI;
using Sensus.Probes;
using Sensus.Context;
using Sensus.Exceptions;
using Sensus.iOS.Context;
using UIKit;
using Foundation;
using Facebook.CoreKit;
using Syncfusion.SfChart.XForms.iOS.Renderers;
using Sensus.iOS.Callbacks.UILocalNotifications;
using Sensus.iOS.Callbacks;
using UserNotifications;
using Sensus.iOS.Callbacks.UNUserNotifications;
using Sensus.iOS.Concurrent;
using Sensus.Encryption;
using System.Threading;
using System.Threading.Tasks;

namespace Sensus.iOS
{
    // The UIApplicationDelegate for the application. This class is responsible for launching the
    // User Interface of the application, as well as listening (and optionally responding) to
    // application events from iOS.
    [Register("AppDelegate")]
    public class AppDelegate : FormsApplicationDelegate
    {
        private readonly object _onActivatedLocker = new object();
        private bool _authorizingAndConfiguringNotifications = false;
        private TaskCompletionSource<UIUserNotificationSettings> _uiUserNotificationSettingsRegistrationTask;

        public override bool FinishedLaunching(UIApplication uiApplication, NSDictionary launchOptions)
        {
            UIDevice.CurrentDevice.BatteryMonitoringEnabled = true;

            SensusContext.Current = new iOSSensusContext
            {
                Platform = Sensus.Context.Platform.iOS,
                MainThreadSynchronizer = new MainConcurrent(),
                SymmetricEncryption = new SymmetricEncryption(SensusServiceHelper.ENCRYPTION_KEY),
                PowerConnectionChangeListener = new iOSPowerConnectionChangeListener()
            };

            // must come after context initialization
            SensusServiceHelper.Initialize(() => new iOSSensusServiceHelper());

            // facebook settings
            Settings.AppID = "873948892650954";
            Settings.DisplayName = "Sensus";

            Forms.Init();
            FormsMaps.Init();
            MapExtendRenderer.Init();
            new SfChartRenderer();
            ZXing.Net.Mobile.Forms.iOS.Platform.Init();

            LoadApplication(new App());

#if UI_TESTING
            Forms.ViewInitialized += (sender, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.View.StyleId))
                {
                    e.NativeView.AccessibilityIdentifier = e.View.StyleId;
                }
            };

            Calabash.Start();
#endif

            return base.FinishedLaunching(uiApplication, launchOptions);
        }

        public override bool OpenUrl(UIApplication application, NSUrl url, string sourceApplication, NSObject annotation)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                if (url != null)
                {
                    if (url.PathExtension == "json")
                    {
                        Protocol protocol = null;

                        if (url.Scheme == "sensus")
                        {
                            try
                            {
                                protocol = await Protocol.DeserializeAsync(new Uri("http://" + url.AbsoluteString.Substring(url.AbsoluteString.IndexOf('/') + 2).Trim()));
                            }
                            catch (Exception ex)
                            {
                                SensusServiceHelper.Get().Logger.Log("Failed to display Sensus Protocol from HTTP URL \"" + url.AbsoluteString + "\":  " + ex.Message, LoggingLevel.Verbose, GetType());
                            }
                        }
                        else if (url.Scheme == "sensuss")
                        {
                            try
                            {
                                protocol = await Protocol.DeserializeAsync(new Uri("https://" + url.AbsoluteString.Substring(url.AbsoluteString.IndexOf('/') + 2).Trim()));
                            }
                            catch (Exception ex)
                            {
                                SensusServiceHelper.Get().Logger.Log("Failed to display Sensus Protocol from HTTPS URL \"" + url.AbsoluteString + "\":  " + ex.Message, LoggingLevel.Verbose, GetType());
                            }
                        }
                        else
                        {
                            try
                            {
                                protocol = await Protocol.DeserializeAsync(File.ReadAllBytes(url.Path));
                            }
                            catch (Exception ex)
                            {
                                SensusServiceHelper.Get().Logger.Log("Failed to display Sensus Protocol from file URL \"" + url.AbsoluteString + "\":  " + ex.Message, LoggingLevel.Verbose, GetType());
                            }
                        }

                        await Protocol.DisplayAndStartAsync(protocol);
                    }
                }
            });

            // We need to handle URLs by passing them to their own OpenUrl in order to make the Facebook SSO authentication works.
            return ApplicationDelegate.SharedInstance.OpenUrl(application, url, sourceApplication, annotation);
        }

        public override void OnActivated(UIApplication uiApplication)
        {
            base.OnActivated(uiApplication);

            // use a gatekeeper for singleton access to the notification authorization/configuration
            lock (_onActivatedLocker)
            {
                if (_authorizingAndConfiguringNotifications)
                {
                    return;
                }
                else
                {
                    _authorizingAndConfiguringNotifications = true;
                }
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await SensusContext.Current.MainThreadSynchronizer.ExecuteThreadSafe(async () =>
                    {
                        // temporarily disable the master page to prevent the user from tapping around before notifications are set up.
                        (App.Current as App).MasterPage.IsEnabled = false;

                        bool notificationsAuthorizedAndConfigured = false;

                        if (UIDevice.CurrentDevice.CheckSystemVersion(10, 0))
                        {
                            // if notifications were previously authorized and configured, there's nothing more to do.
                            UNNotificationSettings settings = await UNUserNotificationCenter.Current.GetNotificationSettingsAsync();

                            if (settings.BadgeSetting == UNNotificationSetting.Enabled &&
                                settings.SoundSetting == UNNotificationSetting.Enabled &&
                                settings.AlertSetting == UNNotificationSetting.Enabled &&
                                UNUserNotificationCenter.Current.Delegate != null &&
                                SensusContext.Current.CallbackScheduler != null &&
                                SensusContext.Current.Notifier != null)
                            {
                                notificationsAuthorizedAndConfigured = true;
                            }
                            else
                            {
                                // request authorization for notifications. if the user previously denied authorization, this will simply return non-granted.
                                Tuple<bool, NSError> grantedError = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Badge | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Alert);

                                // if the user just granted authorization, configure the notification subsystem.
                                if (grantedError.Item1)
                                {
                                    // register for push notifications. it makes sense to do this here because callback scheduling (set up below) depends on it.
                                    UIApplication.SharedApplication.RegisterForRemoteNotifications();

                                    // clear notifications and set up scheduler / notifier
                                    UNUserNotificationCenter.Current.RemoveAllDeliveredNotifications();
                                    UNUserNotificationCenter.Current.RemoveAllPendingNotificationRequests();
                                    UNUserNotificationCenter.Current.Delegate = new UNUserNotificationDelegate();
                                    SensusContext.Current.CallbackScheduler = new UNUserNotificationCallbackScheduler();
                                    SensusContext.Current.Notifier = new UNUserNotificationNotifier();

                                    notificationsAuthorizedAndConfigured = true;
                                }
                            }
                        }
                        // use the pre-10.0 approach based on UILocalNotifications. we require ios 9 or later, so we don't have to worry about 
                        // pre-8 ios as done here:  https://docs.microsoft.com/en-us/azure/notification-hubs/xamarin-notification-hubs-ios-push-notification-apns-get-started
                        else
                        {
                            // if notifications were previously authorized and configured, there's nothing more to do.
                            UIUserNotificationSettings settings = uiApplication.CurrentUserNotificationSettings;
                            if (settings.Types == (UIUserNotificationType.Badge | UIUserNotificationType.Sound | UIUserNotificationType.Alert) &&
                                SensusContext.Current.CallbackScheduler != null &&
                                SensusContext.Current.Notifier != null)
                            {
                                notificationsAuthorizedAndConfigured = true;
                            }
                            else
                            {
                                // request authorization for notifications. if the user previously denied authorization, this will simply return non-granted.
                                _uiUserNotificationSettingsRegistrationTask = new TaskCompletionSource<UIUserNotificationSettings>();
                                UIUserNotificationSettings notificationSettings = UIUserNotificationSettings.GetSettingsForTypes(UIUserNotificationType.Badge | UIUserNotificationType.Sound | UIUserNotificationType.Alert, new NSSet());
                                UIApplication.SharedApplication.RegisterUserNotificationSettings(notificationSettings);
                                settings = await _uiUserNotificationSettingsRegistrationTask.Task;

                                // if the user just granted authorization, configure the notification subsystem.
                                if (settings.Types == (UIUserNotificationType.Badge | UIUserNotificationType.Sound | UIUserNotificationType.Alert))
                                {
                                    // register for push notifications. it makes sense to do this here because callback scheduling (set up below) depends on it.
                                    UIApplication.SharedApplication.RegisterForRemoteNotifications();

                                    // set up scheduler / notifier
                                    SensusContext.Current.CallbackScheduler = new UILocalNotificationCallbackScheduler();
                                    SensusContext.Current.Notifier = new UILocalNotificationNotifier();

                                    notificationsAuthorizedAndConfigured = true;
                                }
                            }
                        }

                        if (notificationsAuthorizedAndConfigured)
                        {
                            // ensure service helper is running
                            await SensusServiceHelper.Get().StartAsync();

                            // update/run all callbacks
                            await (SensusContext.Current.CallbackScheduler as IiOSCallbackScheduler).UpdateCallbacksAsync();

                            // reenable the master page to let the user proceed
                            (App.Current as App).MasterPage.IsEnabled = true;

#if UI_TESTING
                            // load and run the UI testing protocol
                            string filePath = NSBundle.MainBundle.PathForResource("UiTestingProtocol", "json");
                            using (Stream file = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                            {
                                await Protocol.RunUiTestingProtocolAsync(file);
                            }
#endif
                        }
                        else
                        {
                            UIAlertView warning = new UIAlertView("Warning", "Sensus will not run with notifications disabled. Please enable notifications.", default(IUIAlertViewDelegate), "Close", "Open Settings");

                            warning.Dismissed += (sender, e) =>
                            {
                                if (e.ButtonIndex == 1)
                                {
                                    uiApplication.OpenUrl(new NSUrl(UIApplication.OpenSettingsUrlString.ToString()));
                                }
                            };

                            warning.Show();
                        }
                    });
                }
                catch (Exception ex)
                {
                    SensusException.Report("Exception while authorizing/configuring notifications:  " + ex.Message, ex);
                }
                finally
                {
                    // ensure that we always release the gatekeeper
                    _authorizingAndConfiguringNotifications = false;
                }
            });
        }

        /// <summary>
        /// Pre-10.0:  Handles local notification registration results.
        /// </summary>
        /// <param name="application">Application.</param>
        /// <param name="notificationSettings">Notification settings.</param>
        public override void DidRegisterUserNotificationSettings(UIApplication application, UIUserNotificationSettings notificationSettings)
        {
            // the variable should never be null, but just in case...
            _uiUserNotificationSettingsRegistrationTask?.SetResult(notificationSettings);
        }

        /// <summary>
        /// Pre-10.0:  Handles notifications received when the app is in the foreground. See <see cref="UNUserNotificationDelegate.WillPresentNotification"/> for
        /// the corresponding handler for iOS 10.0 and above.
        /// </summary>
        /// <param name="application">Application.</param>
        /// <param name="notification">Notification.</param>
        public override void ReceivedLocalNotification(UIApplication application, UILocalNotification notification)
        {
            // UILocalNotifications were obsoleted in iOS 10.0, and we should not be receiving them via this app delegate
            // method. we won't have any idea how to service them on iOS 10.0 and above. report the problem and bail.
            if (UIDevice.CurrentDevice.CheckSystemVersion(10, 0))
            {
                SensusException.Report("Received UILocalNotification in iOS 10 or later.");
            }
            else
            {
                // we're in iOS < 10.0, which means we should have a notifier and scheduler to handle the notification.

                // cancel notification (removing it from the tray), since it has served its purpose
                (SensusContext.Current.Notifier as IUILocalNotificationNotifier)?.CancelNotification(notification);

                iOSCallbackScheduler callbackScheduler = SensusContext.Current.CallbackScheduler as iOSCallbackScheduler;

                if (callbackScheduler == null)
                {
                    SensusException.Report("We don't have an iOSCallbackScheduler.");
                }
                else if (notification.UserInfo == null)
                {
                    SensusException.Report("Null user info passed to ReceivedLocalNotification.");
                }
                else
                {
                    // run asynchronously to release the UI thread
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        // we've tried pulling some of the code below out of the UI thread, but we do not receive/process
                        // the callback notifications when doing so..
                        await SensusContext.Current.MainThreadSynchronizer.ExecuteThreadSafe(async () =>
                        {
                            // service the callback if we've got one (not all notification userinfo bundles are for callbacks)
                            if (callbackScheduler.IsCallback(notification.UserInfo))
                            {
                                await callbackScheduler.ServiceCallbackAsync(notification.UserInfo);
                            }

                            // check whether the user opened the notification to open sensus, indicated by an application state that is not active. we'll
                            // also get notifications when the app is active, since we use them for timed callback events.
                            if (application.ApplicationState != UIApplicationState.Active)
                            {
                                // if the user opened the notification, display the page associated with the notification, if any. 
                                callbackScheduler.OpenDisplayPage(notification.UserInfo);

                                // provide some generic feedback if the user responded to a silent callback notification
                                if (callbackScheduler.TryGetCallback(notification.UserInfo)?.Silent ?? false)
                                {
                                    await SensusServiceHelper.Get().FlashNotificationAsync("Study Updated.");
                                }
                            }
                        });
                    });
                }
            }
        }

        public override void RegisteredForRemoteNotifications(UIApplication application, NSData deviceToken)
        {
            iOSSensusServiceHelper serviceHelper = SensusServiceHelper.Get() as iOSSensusServiceHelper;
            serviceHelper.PushNotificationTokenData = deviceToken;

            // update push notification registrations. this depends on internet connectivity to S3
            // so it might hang if connectivity is poor. ensure we don't violate execution limits.
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            nint updateTaskId = application.BeginBackgroundTask(() =>
            {
                cancellationTokenSource.Cancel();
            });

            serviceHelper.UpdatePushNotificationRegistrationsAsync(cancellationTokenSource.Token).ContinueWith(finishedTask =>
            {
                application.EndBackgroundTask(updateTaskId);
            });
        }

        public override void FailedToRegisterForRemoteNotifications(UIApplication application, NSError error)
        {
            SensusException.Report("Failed to register for remote notifications.", error == null ? null : new Exception(error.ToString()));
        }

        public override void ReceivedRemoteNotification(UIApplication application, NSDictionary userInfo)
        {
            ProcessRemoteNotification(userInfo);
        }

        public override void DidReceiveRemoteNotification(UIApplication application, NSDictionary userInfo, Action<UIBackgroundFetchResult> completionHandler)
        {
            ProcessRemoteNotification(userInfo);
            completionHandler?.Invoke(UIBackgroundFetchResult.NewData);
        }

        private void ProcessRemoteNotification(NSDictionary userInfo)
        {
            if (userInfo.ContainsKey(new NSString("command")))
            {
                string command = (userInfo[new NSString("command")] as NSString).ToString();
                SensusServiceHelper.Get().Logger.Log("Received push notification command:  " + command, LoggingLevel.Normal, GetType());
            }
        }

        // This method should be used to release shared resources and it should store the application state.
        // If your application supports background exection this method is called instead of WillTerminate
        // when the user quits.
        public override void DidEnterBackground(UIApplication uiApplication)
        {
            // scheduler will be null in the case where notifications have not been authorized
            (SensusContext.Current.CallbackScheduler as iOSCallbackScheduler)?.CancelSilentNotifications();

            iOSSensusServiceHelper serviceHelper = SensusServiceHelper.Get() as iOSSensusServiceHelper;

            // save app state in background
            nint saveTaskId = uiApplication.BeginBackgroundTask(() =>
            {
                // not much we can do if we run out of time...
            });

            serviceHelper.SaveAsync().ContinueWith(finishedTask =>
            {
                uiApplication.EndBackgroundTask(saveTaskId);
            });
        }

        // This method is called as part of the transiton from background to active state.
        public override void WillEnterForeground(UIApplication uiApplication)
        {
        }

        // This method is called when the application is about to terminate. Save data, if needed.
        public override void WillTerminate(UIApplication uiApplication)
        {
            // this method won't be called when the user kills the app using multitasking; however,
            // it should be called if the system kills the app when it's running in the background.
            // it should also be called if the system shuts down due to loss of battery power.
            // there doesn't appear to be a way to gracefully stop the app when the user kills it
            // via multitasking...we'll have to live with that. also some online resources indicate 
            // that no background time can be requested from within this method. so, instead of 
            // beginning a background task, just wait for the calls to finish.

            SensusServiceHelper serviceHelper = SensusServiceHelper.Get();

            // we're going to save the service helper and its protocols/probes in the running state
            // so that they will be restarted if/when the user restarts the app. in order to properly 
            // track running time for listening probes, we need to add a stop time manually since
            // we won't call stop until after the service helper has been saved.
            foreach (Protocol protocol in serviceHelper.RegisteredProtocols)
            {
                if (protocol.Running)
                {
                    foreach (Probe probe in protocol.Probes)
                    {
                        if (probe.Running)
                        {
                            lock (probe.StartStopTimes)
                            {
                                probe.StartStopTimes.Add(new Tuple<bool, DateTime>(false, DateTime.Now));
                            }
                        }
                    }
                }
            }

            serviceHelper.Save();
            serviceHelper.StopProtocols();
        }
    }
}