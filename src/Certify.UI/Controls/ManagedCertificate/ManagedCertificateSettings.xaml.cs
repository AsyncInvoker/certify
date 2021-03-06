﻿using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Certify.Locales;
using Certify.Models;
using Certify.Shared.Utils;
using MahApps.Metro.Controls;

namespace Certify.UI.Controls.ManagedCertificate
{
    /// <summary>
    /// Interaction logic for ManagedCertificateSettings.xaml 
    /// </summary>
    public partial class ManagedCertificateSettings : UserControl
    {
        protected Certify.UI.ViewModel.AppViewModel AppViewModel => UI.ViewModel.AppViewModel.Current;

        protected Certify.UI.ViewModel.ManagedCertificateViewModel ItemViewModel => UI.ViewModel.ManagedCertificateViewModel.Current;

        protected Models.Providers.ILog Log => AppViewModel.Log;

        public ManagedCertificateSettings()
        {
            InitializeComponent();
            AppViewModel.PropertyChanged += MainViewModel_PropertyChanged;

            ToggleAdvancedView();
        }

        private void MainViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "SelectedItem")
            {
                SettingsTab.SelectedIndex = 0;

                // show status tab for existing managed certs
                var showStatus = ItemViewModel.SelectedItem?.Id != null && ItemViewModel.SelectedItem.DateLastRenewalAttempt != null;

                if (showStatus)
                {
                    TabStatusInfo.Visibility = Visibility.Visible;
                    SettingsTab.SelectedItem = TabStatusInfo;
                }
                else
                {
                    TabStatusInfo.Visibility = Visibility.Collapsed;
                    SettingsTab.SelectedItem = TabDomains;
                }

                // TODO: fix property changed dependencies
                ItemViewModel.RaiseSelectedItemChanges();

                AppViewModel.IsChanged = false;

                ToggleAdvancedView();
            }
        }

        private void ShowValidationError(string msg)
        {
            (App.Current as App).ShowNotification(msg, App.NotificationType.Error, true);
        }
        private async Task<bool> ValidateAndSave(Models.ManagedCertificate item)
        {
            /*if (item.Id == null && MainViewModel.SelectedWebSite == null)
            {
                MessageBox.Show(SR.ManagedCertificateSettings_SelectWebsiteOrCert, SR.SaveError, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }*/

            if (item.RequestConfig.Challenges == null)
            {
                item.RequestConfig.Challenges = new System.Collections.ObjectModel.ObservableCollection<CertRequestChallengeConfig>();
            }

            if (item.Id == null && item.RequestConfig.Challenges.Any(c => c.ChallengeType == SupportedChallengeTypes.CHALLENGE_TYPE_SNI))
            {
                ShowValidationError("Sorry, the tls-sni-01 challenge type is no longer supported for new certificates.");
                return false;
            }

            if (string.IsNullOrEmpty(item.Name))
            {
                ShowValidationError(SR.ManagedCertificateSettings_NameRequired);
                return false;
            }

            // check primary domain is also checked
            if (ItemViewModel.PrimarySubjectDomain != null && ItemViewModel.SelectedItem.DomainOptions.Any())
            {
                var primaryDomain = ItemViewModel.SelectedItem.DomainOptions.FirstOrDefault(d => d.IsPrimaryDomain);
                if (primaryDomain != null && !primaryDomain.IsSelected)
                {
                    primaryDomain.IsSelected = true;
                }
            }

            // no primary domain selected, try to auto select first checked domain
            if (ItemViewModel.PrimarySubjectDomain == null && ItemViewModel.SelectedItem.DomainOptions.Any(d => d.IsSelected))
            {
                var autoPrimaryDomain = ItemViewModel.SelectedItem.DomainOptions.First(d => d.IsSelected);
                autoPrimaryDomain.IsPrimaryDomain = true;
            }

            // a primary subject domain must be set
            if (ItemViewModel.PrimarySubjectDomain == null)
            {
                // if we still can't decide on the primary domain ask user to define it
                MessageBox.Show(SR.ManagedCertificateSettings_NeedPrimaryDomain, SR.SaveError, MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (ItemViewModel.SelectedItem.DomainOptions.Any(d => d.IsSelected && (!d.Domain.Contains(".") || d.Domain.ToLower().EndsWith(".local"))))
            {
                // one or more selected domains does not include a label seperator (is an internal host name) or end in .local
                ShowValidationError("One or more domains specified are internal hostnames. Certificates for internal host names are not supported by the Certificate Authority.");
                return false;
            }

            // if title set to the default, use the primary domain
            if (item.Name == SR.ManagedCertificateSettings_DefaultTitle)
            {
                item.Name = ItemViewModel.PrimarySubjectDomain.Domain;
            }

            // certificates cannot request wildcards unless they also use DNS validation
            if (
                item.DomainOptions.Any(d => d.IsSelected && d.Domain.StartsWith("*."))
                &&
                !item.RequestConfig.Challenges.Any(c => c.ChallengeType == SupportedChallengeTypes.CHALLENGE_TYPE_DNS)
                )
            {

                ShowValidationError("Wildcard domains cannot use http-01 validation for domain authorization. Use dns-01 instead.");
                return false;
            }

            // TLS-SNI-01 (deprecated)
            if (item.RequestConfig.Challenges.Any(c => c.ChallengeType == SupportedChallengeTypes.CHALLENGE_TYPE_SNI))
            {
                ShowValidationError("The tls-sni-01 challenge type is no longer available. You need to switch to either http-01 or dns-01.");
                return false;
            }

            if (item.RequestConfig.Challenges.Any(c => c.ChallengeType == SupportedChallengeTypes.CHALLENGE_TYPE_DNS && c.ChallengeProvider == null))
            {
                ShowValidationError("The dns-01 challenge type requires a DNS Update Method selection.");
                return false;
            }

            if (item.RequestConfig.Challenges.Count(c => string.IsNullOrEmpty(c.DomainMatch)) > 1)
            {
                ShowValidationError("Only one authorization configuration can be used match any domain (domain match blank). Specify domain(s) to match or remove additional configuration. ");
                return false;
            }

            // validate settings for authorizations non-optional parmaeters
            foreach (var c in item.RequestConfig.Challenges)
            {
                if (c.Parameters != null && c.Parameters.Any())
                {
                    //validate parameters
                    foreach (var p in c.Parameters)
                    {
                        if (p.IsRequired && string.IsNullOrEmpty(p.Value))
                        {
                            ShowValidationError($"Challenge configuration parameter required: {p.Name}");
                            return false;
                        }
                    }
                }
            }

            // check certificate will not exceed 100 name limit
            var numSelectedDomains = item.DomainOptions.Count(d => d.IsSelected);

            if (numSelectedDomains > 100)
            {
                ShowValidationError($"Certificates cannot include more than 100 names. You will need to remove names or split your certificate into 2 or more managed certificates.");
                return false;
            }

            if (item.RequestConfig.PerformAutomatedCertBinding)
            {
                item.RequestConfig.BindingIPAddress = null;
                item.RequestConfig.BindingPort = null;
                item.RequestConfig.BindingUseSNI = null;
            }
            else
            {
                //always select Use SNI unless it's specifically set to false
                if (item.RequestConfig.BindingUseSNI == null)
                {
                    item.RequestConfig.BindingUseSNI = true;
                }

                // if user has chosen to bind SNI with a specific IP, warn and confirm save
                if (item.RequestConfig.BindingUseSNI == true && !string.IsNullOrEmpty(item.RequestConfig.BindingIPAddress) && item.RequestConfig.BindingIPAddress != "*")
                {
                    if (MessageBox.Show(SR.ManagedCertificateSettings_InvalidSNI, SR.SaveError, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        // opted not to save
                        return false;
                    };
                }
            }

            if (!string.IsNullOrEmpty(item.RequestConfig.WebhookTrigger) &&
                item.RequestConfig.WebhookTrigger != Webhook.ON_NONE)
            {
                if (string.IsNullOrEmpty(item.RequestConfig.WebhookUrl) ||
                    !Uri.TryCreate(item.RequestConfig.WebhookUrl, UriKind.Absolute, out var uri))
                {
                    ShowValidationError(SR.ManagedCertificateSettings_HookMustBeValidUrl);
                    return false;
                }
                if (string.IsNullOrEmpty(item.RequestConfig.WebhookMethod))
                {
                    ShowValidationError(SR.ManagedCertificateSettings_HookMethodMustBeSet);
                    return false;
                }
            }
            else
            {
                // clear out saved values if setting webhook to NONE
                item.RequestConfig.WebhookUrl = null;
                item.RequestConfig.WebhookMethod = null;
                item.RequestConfig.WebhookContentType = null;
                item.RequestConfig.WebhookContentBody = null;
            }

            //creating new managed item
            return await ItemViewModel.SaveManagedCertificateChanges();
        }

        private async void Button_Save(object sender, RoutedEventArgs e)
        {
            if (ItemViewModel.SelectedItem.IsChanged)
            {
                var item = ItemViewModel.SelectedItem;
                await ValidateAndSave(item);
            }
            else
            {
                MessageBox.Show(SR.ManagedCertificateSettings_NoChanges);
            }
        }

        private async void Button_DiscardChanges(object sender, RoutedEventArgs e)
        {
            //if new item, discard and select first item in managed sites
            if (ItemViewModel.SelectedItem.Id == null)
            {
                ReturnToDefaultManagedCertificateView();
            }
            else
            {
                //reload settings for managed sites, discard changes
                await ItemViewModel.DiscardChanges();

                ReturnToDefaultManagedCertificateView();
            }
        }

        private void ReturnToDefaultManagedCertificateView() => ItemViewModel.SelectedItem = null;

        private async void Button_RequestCertificate(object sender, RoutedEventArgs e)
        {
            if (ItemViewModel.SelectedItem != null)
            {

                var savedOK = await ValidateAndSave(ItemViewModel.SelectedItem);
                if (!savedOK)
                {
                    return;
                }

                //begin request
                AppViewModel.MainUITabIndex = (int)MainWindow.PrimaryUITabs.CurrentProgress;

                var result = await AppViewModel.BeginCertificateRequest(ItemViewModel.SelectedItem.Id);
                if (result != null)
                {
                    if (result.IsSuccess == false && result.Result is Exception)
                    {
                        var msg = ((Exception)result.Result)?.ToString();
                        Log?.Error($"RequestCertificate: {msg}");
                    }
                }

                ItemViewModel.RaisePropertyChangedEvent(nameof(ItemViewModel.SelectedItemLogEntries));
            }
        }

        private async void Button_Delete(object sender, RoutedEventArgs e)
        {
            await AppViewModel.DeleteManagedCertificate(ItemViewModel.SelectedItem);
            if (ItemViewModel.SelectedItem?.Id == null)
            {
                AppViewModel.SelectedItem = AppViewModel.ManagedCertificates.FirstOrDefault();
            }
        }

        private void ShowTestResultsUI()
        {
            var parentWindow = Window.GetWindow(this);
            var obj = parentWindow.FindName("MainFlyout");
            var flyout = (Flyout)obj;
            flyout.Header = "Test Progress";
            flyout.Content = new TestProgress();
            flyout.IsOpen = !flyout.IsOpen;
        }

        private async void TestChallenge_Click(object sender, EventArgs e)
        {
            if (ItemViewModel.IsTestInProgress)
            {
                ShowTestResultsUI();
                return;
            }

            // validate and save before test
            if (!await ValidateAndSave(ItemViewModel.SelectedItem))
            {
                return;
            }

            var challengeConfig = ItemViewModel.SelectedItem.GetChallengeConfig(null);

            if (challengeConfig.ChallengeType == SupportedChallengeTypes.CHALLENGE_TYPE_HTTP && !string.IsNullOrEmpty(ItemViewModel.SelectedItem.ServerSiteId) && !AppViewModel.IsIISAvailable)
            {
                MessageBox.Show(SR.ManagedCertificateSettings_CannotChallengeWithoutIIS, SR.ChallengeError, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else if (challengeConfig.ChallengeType != null)
            {
                ItemViewModel.IsTestInProgress = true;
                Button_TestChallenge.IsEnabled = false;

                try
                {
                    ItemViewModel.UpdateManagedCertificateSettings();
                }
                catch (Exception exp)
                {
                    // usual failure is that primary domain is not set
                    Button_TestChallenge.IsEnabled = true;
                    ItemViewModel.IsTestInProgress = false;

                    MessageBox.Show(exp.Message);
                    return;
                }

                ItemViewModel.ConfigCheckResults = new System.Collections.ObjectModel.ObservableCollection<StatusMessage> {
                    new StatusMessage{IsOK=true, Message="Testing in progress.."}
                };

                AppViewModel.ClearRequestProgressResults();

                ShowTestResultsUI();

                // begin listening for progress info
                AppViewModel.TrackProgress(ItemViewModel.SelectedItem);

                var results = await ItemViewModel.TestChallengeResponse(ItemViewModel.SelectedItem);
                ItemViewModel.ConfigCheckResults =
                    new System.Collections.ObjectModel.ObservableCollection<StatusMessage>(results);

                ItemViewModel.RaisePropertyChangedEvent(nameof(ItemViewModel.ConfigCheckResults));

                //TODO: just use viewmodel to determine if test button should be enabled
                Button_TestChallenge.IsEnabled = true;
                ItemViewModel.IsTestInProgress = false;
            }
        }

        private void Dismiss_Click(object sender, RoutedEventArgs e) => AppViewModel.SelectedItem = null;

        private void CheckAdvancedView_Checked(object sender, RoutedEventArgs e) => ToggleAdvancedView();

        private void ToggleAdvancedView()
        {
            if (ItemViewModel.SelectedItem != null &&
                (!string.IsNullOrEmpty(ItemViewModel.SelectedItem.RequestConfig.PostRequestPowerShellScript)
                ||
                !string.IsNullOrEmpty(ItemViewModel.SelectedItem.RequestConfig.PreRequestPowerShellScript)
                ||
                !string.IsNullOrEmpty(ItemViewModel.SelectedItem.RequestConfig.WebhookUrl))
                )
            {
                // force advanced view to show if using scripting or webhooks
                CheckAdvancedView.IsChecked = true;
            }

            if (CheckAdvancedView.IsChecked == false)
            {
                TabScripting.Visibility = Visibility.Collapsed;
                TabOptions.Visibility = Visibility.Collapsed;
            }
            else
            {
                TabScripting.Visibility = Visibility.Visible;
                TabOptions.Visibility = Visibility.Visible;
            }
        }
    }
}
