// Vendored from HmsTeam.UiPath.VS.Projects/Mail/Core/Exchange.cs when SuperTrigger.Web was split into
// its own repository. Only the EWS-based mail polling API (used by MailPollingService) is needed here
// — the UiPath activity wrapper and its WPF/UiPath.Workflow dependencies were left behind.
using HMS.Shared;
using Microsoft.Exchange.WebServices.Data;
using Microsoft.Identity.Client;
using Newtonsoft.Json.Linq;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace HMS.Mail.Exchange
{
    public class Core
    {
        public static ExchangeService CreateExchangeService(string ExchangeMailServerURL, ExchangeVersion serverVersion, bool ToUseWindowsAuthentication, ref NLog.Logger log, bool consoleLogging = true, string UserDomain = null, string UserName = null, SecureString Password = null, string SharedMailBox = null, IConfidentialClientApplication ccaa = null)
        {
			System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls11 | System.Net.SecurityProtocolType.Tls;

            ExchangeService service = new ExchangeService();


            //new Uri(ExchangeMailServerURL).Host.ToLower() != "outlook.office365.com"'
            if (ccaa == null)
            {
                service = new ExchangeService(serverVersion);

                service.Url = new Uri(ExchangeMailServerURL);

                log.Debug("Exchange SSO?:" + ToUseWindowsAuthentication);
                if (consoleLogging)
                    Console.WriteLine("Exchange SSO?:" + ToUseWindowsAuthentication);


                if (ToUseWindowsAuthentication)
                    service.UseDefaultCredentials = true;
                else if (String.IsNullOrEmpty(UserDomain))
                    service.Credentials = new WebCredentials(UserName, new System.Net.NetworkCredential(string.Empty, Password).Password);
                else
                    service.Credentials = new WebCredentials(UserName, new System.Net.NetworkCredential(string.Empty, Password).Password, UserDomain);
            }
            else
            {
                service = new ExchangeService(ExchangeVersion.Exchange2013_SP1);

                service.Url = new Uri("https://outlook.office365.com/EWS/Exchange.asmx");

                log.Debug("Access via 365 token...");
                if (consoleLogging)
                    Console.WriteLine("Access via 365 token...");
                var cca =  ConfidentialClientApplicationBuilder
                        .Create(ccaa.AppConfig.ClientId)
                        .WithClientSecret(ccaa.AppConfig.ClientSecret)
                        .WithTenantId(ccaa.AppConfig.TenantId)
                        //.WithRedirectUri("urn:ietf:wg:oauth:2.0:oob")
                        .Build();

                var ewsScopes = new String[] { "https://outlook.office365.com/.default" }; 

                string result = GetToken(cca, ewsScopes).Result;

                service.Credentials = new OAuthCredentials(result);

                service.ImpersonatedUserId = new ImpersonatedUserId(ConnectingIdType.SmtpAddress, SharedMailBox);

            }

            Microsoft.Exchange.WebServices.Data.FolderView view = new Microsoft.Exchange.WebServices.Data.FolderView(1);
            view.PropertySet = new PropertySet(BasePropertySet.IdOnly);
            view.PropertySet.Add(FolderSchema.DisplayName);
            SearchFilter searchFilter = new SearchFilter.IsGreaterThan(FolderSchema.TotalCount, 0);
            view.Traversal = FolderTraversal.Deep;

            FindFoldersResults folders = (String.IsNullOrEmpty(SharedMailBox) ? service.FindFolders(WellKnownFolderName.Root, searchFilter, view) : service.FindFolders(new FolderId(WellKnownFolderName.Root, SharedMailBox), searchFilter, view));

            return service;

        }

        public static async Task<String> GetToken(IConfidentialClientApplication cca, string[] scopes)
        {
            return cca.AcquireTokenForClient(scopes).ExecuteAsync().Result.AccessToken;
        }

        public static void MarkMessageAsReadOrUnRead(ExchangeService service, MailMessage mail, bool read, ref NLog.Logger log, bool consoleLogging = true)
        {
            EmailMessage message = EmailMessage.Bind(service, mail.Headers["UID"], new PropertySet(BasePropertySet.FirstClassProperties, EmailMessageSchema.IsRead));
            message.IsRead = true;
            message.Update(ConflictResolutionMode.AutoResolve);
            log.Info("Mail marked as " + (read ? "read" : "unread"));
            if (consoleLogging)
                Console.WriteLine("Mail marked as " + (read ? "read" : "unread"));
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="ExchangeMailServerURL"></param>
        /// <param name="serverVersion"></param>
        /// <param name="ToUseWindowsAuthentication"></param>
        /// <param name="MailFolderToLookIn"></param>
        /// <param name="OnlyUnReadMails"></param>
        /// <param name="log"></param>
        /// <param name="consoleLogging"></param>
        /// <param name="MinDateTime"></param>
        /// <param name="MaxDateTime"></param>
        /// <param name="ToBringAttachments"></param>
        /// <param name="SharedMailBox"></param>
        /// <param name="UserDomain"></param>
        /// <param name="UserName"></param>
        /// <param name="Password"></param>
        /// <param name="SpecificMailUID"></param>
        /// <param name="SubjectContainsFilter"></param>
        /// <param name="SenderFilter">Wildcards supported</param>
        /// <param name="ReceiverFilter"></param>
        /// <param name="BodyContainsFilter"></param>
        /// <param name="ToMarkAsRead"></param>
        /// <param name="sortMethod"></param>
        /// <param name="GetBodyInHTML"></param>
        /// <param name="AttachmentsContainsTypeFilter"></param>
        /// <returns></returns>

        public static (List<MailMessage>, ExchangeService) GetMessages(string ExchangeMailServerURL, ExchangeVersion serverVersion, bool ToUseWindowsAuthentication, string MailFolderToLookIn, bool OnlyUnReadMails, ref NLog.Logger log, bool consoleLogging = true, DateTime? MinDateTime = null, DateTime? MaxDateTime = null, bool ToBringAttachments = true, string SharedMailBox = null, string UserDomain = null, string UserName = null, SecureString Password = null, string SpecificMailUID = null, string SubjectContainsFilter = null, string[] SenderFilter = null, string ReceiverFilter = null, string BodyContainsFilter = null, bool ToMarkAsRead = true, SortDirection sortMethod = SortDirection.Ascending, bool GetBodyInHTML = false, string[] AttachmentsContainsTypeFilter = null, IConfidentialClientApplication ccaa = null)
        {
            log.Debug("Trying to get mails...");
            if (consoleLogging)
                Console.WriteLine("Trying to get mails...");
            List<MailMessage> Mails = new List<MailMessage>();

            ExchangeService service = CreateExchangeService(ExchangeMailServerURL, serverVersion, ToUseWindowsAuthentication, ref log, consoleLogging, UserDomain, UserName, Password, SharedMailBox, ccaa);





            //service.TraceEnabled = true;
            //service.TraceFlags = TraceFlags.All;

            //Finding the appropriate folder

            List<SearchFilter> searchFilterCollectionFolder = new List<SearchFilter>();

            Microsoft.Exchange.WebServices.Data.FolderView folderView = new Microsoft.Exchange.WebServices.Data.FolderView(1);

            ExtendedPropertyDefinition PR_Folder_Path = new ExtendedPropertyDefinition(26293, MapiPropertyType.String);

            folderView.PropertySet = new PropertySet(FolderSchema.Id, FolderSchema.DisplayName) { PR_Folder_Path };

            FindFoldersResults findFolderResults = null;

            SearchFilter searchFilterFolder = null;

            Microsoft.Exchange.WebServices.Data.Folder currentFolder = null;

            String[] folderStrings = MailFolderToLookIn.Split('\\');

            Object fpPath = null;

            string fpPathString = "";

            foreach (String folderString in folderStrings)
            {

                searchFilterCollectionFolder.Clear();

                searchFilterCollectionFolder.Add(new SearchFilter.IsEqualTo(FolderSchema.DisplayName, folderString));

                searchFilterFolder = new SearchFilter.SearchFilterCollection(LogicalOperator.And, searchFilterCollectionFolder.ToArray());

                if (currentFolder == null)
                {
                    findFolderResults = (String.IsNullOrEmpty(SharedMailBox) ? service.FindFolders(WellKnownFolderName.MsgFolderRoot, searchFilterFolder, folderView) : service.FindFolders(new FolderId(WellKnownFolderName.MsgFolderRoot, SharedMailBox), searchFilterFolder, folderView));
                    try
                    {
                        currentFolder = findFolderResults.Folders[0];
                        currentFolder.TryGetProperty(PR_Folder_Path, out fpPath);
                        fpPathString = Encoding.Unicode.GetString(Functions.HexStringToByteArray(BitConverter.ToString(UnicodeEncoding.Unicode.GetBytes((String)fpPath)).Replace("FE-FF", "5C-00").Replace("-", "")));
                    }
                    catch
                    {
                        throw new Exception("No such folder ("+folderString+") found!");
                        //return;
                    }
                }
                else
                {
                    findFolderResults = (String.IsNullOrEmpty(SharedMailBox) ? service.FindFolders(currentFolder.Id, searchFilterFolder, folderView) : service.FindFolders(currentFolder.Id, searchFilterFolder, folderView));
                    try
                    {
                        currentFolder = findFolderResults.Folders[0];
                        currentFolder.TryGetProperty(PR_Folder_Path, out fpPath);
                        fpPathString=Encoding.Unicode.GetString(Functions.HexStringToByteArray(BitConverter.ToString(UnicodeEncoding.Unicode.GetBytes((String)fpPath)).Replace("FE-FF", "5C-00").Replace("-", "")));
                    }
                    catch
                    {
                        throw new Exception("No such folder (" + fpPathString.Substring(1) + @"\" + folderString + ") found!");
                        //return;
                    }
                }
            }

            //Console.WriteLine("Connected succesfully.");

            if (findFolderResults.Folders.Count == 0)
            {
                throw new Exception("No such folder ("+MailFolderToLookIn+") found!");
                //return;
            }


            List<SearchFilter> searchFilterCollection = new List<SearchFilter>();


            //Definning filters
            if (!String.IsNullOrEmpty(SpecificMailUID))
            {
                searchFilterCollection.Add(new SearchFilter.IsEqualTo(ItemSchema.Id, SpecificMailUID));
            }
            else
            {
                if (!String.IsNullOrEmpty(SubjectContainsFilter))
                    searchFilterCollection.Add(new SearchFilter.ContainsSubstring(ItemSchema.Subject, SubjectContainsFilter));
                //if (SenderFilter_1 != "")
                //    searchFilterCollection.Add(new SearchFilter.IsEqualTo(EmailMessageSchema.Sender, SenderFilter_1));
                if (OnlyUnReadMails)
                    searchFilterCollection.Add(new SearchFilter.IsEqualTo(EmailMessageSchema.IsRead, false));
                if (MinDateTime != null)
                {
                    searchFilterCollection.Add(new SearchFilter.IsGreaterThanOrEqualTo(ItemSchema.DateTimeReceived, MinDateTime));
                }
                if (MaxDateTime != null)
                {

                    searchFilterCollection.Add(new SearchFilter.IsLessThanOrEqualTo(ItemSchema.DateTimeReceived, MaxDateTime));
                }
            }


            SearchFilter searchFilter = new SearchFilter.SearchFilterCollection(LogicalOperator.And, searchFilterCollection.ToArray());

            ItemView view = new ItemView(1000);

            view.PropertySet = new PropertySet(BasePropertySet.IdOnly);

            view.OrderBy.Add(ItemSchema.DateTimeReceived, sortMethod);


            view.Traversal = ItemTraversal.Shallow;

            FindItemsResults<Item> findResults;

            bool more = true;

            //Console.WriteLine("Prepare mails...");

            while (more)
            {
                findResults = (searchFilter == null || searchFilterCollection.Count == 0 ? service.FindItems(findFolderResults.Folders[0].Id, view) : service.FindItems(findFolderResults.Folders[0].Id, searchFilter, view));

                PropertySet propertySet = new PropertySet(BasePropertySet.FirstClassProperties);
                propertySet.RequestedBodyType = new Microsoft.Exchange.WebServices.Data.BodyType?((GetBodyInHTML ? Microsoft.Exchange.WebServices.Data.BodyType.HTML : Microsoft.Exchange.WebServices.Data.BodyType.Text));
                PropertySet propertySet1 = propertySet;



                foreach (Item mailitem in findResults)
                {

                    EmailMessage emailMessage = EmailMessage.Bind(service, mailitem.Id, propertySet1);

                    if (AttachmentsContainsTypeFilter != null && AttachmentsContainsTypeFilter.Count() > 0)
                        if (!emailMessage.Attachments.Any(y => AttachmentsContainsTypeFilter.Select(x => x.Trim().ToLower()).Contains(y.Name.ToLower().Substring(y.Name.LastIndexOf(".") + 1))))
                            continue;
                    /*
                    foreach (var s in emailMessage.Attachments.Where(x => x.Name == null).ToList())
                    {
                        emailMessage.Attachments.Remove(s);
                    }

                    foreach (var s in emailMessage.Attachments.Where(kv => kv.Name.IndexOfAny(Path.GetInvalidFileNameChars()) != -1).ToList())
                    {
                        s.Name = System.Text.RegularExpressions.Regex.Replace(s.Name, @"[""\\/?:*<>|]", "");
                    }
                    */

                    if (ToBringAttachments)
                        if (emailMessage.Attachments.Count > 0)
                        {
                            foreach (Microsoft.Exchange.WebServices.Data.Attachment attachment in emailMessage.Attachments)
                            {
                                attachment.Load();
                            }
                        }
                    
                    Mails.Add(ConvertToMailMessage(emailMessage, ref log, consoleLogging));


                }

                more = findResults.MoreAvailable;

                if (more)
                {
                    view.Offset += 1000;
                }
            }




            if (SenderFilter != null && SenderFilter.Count() > 0)
                Mails = Mails.Where(x => SenderFilter.Select(y => Functions.WildCardToRegular(y.Trim().ToLower())).Any(y => System.Text.RegularExpressions.Regex.IsMatch(x.Sender.Address.ToLower(), y))).ToList<MailMessage>();
            if (!String.IsNullOrEmpty(BodyContainsFilter))
                Mails = Mails.Where(x => x.Body.ToLower().Contains(BodyContainsFilter.ToLower())).ToList<MailMessage>();

            if (!String.IsNullOrEmpty(ReceiverFilter))
                Mails = Mails.Where(x => (x.To.Any(y => y.Address.ToLower().Equals(ReceiverFilter.ToLower()))) || (x.CC.Any(y => y.Address.ToLower().Equals(ReceiverFilter.ToLower()))) || (x.Bcc.Any(y => y.Address.ToLower().Equals(ReceiverFilter.ToLower())))).ToList<MailMessage>();


            log.Debug("Found " + Mails.Count() + " mails...");
            if (consoleLogging)
                Console.WriteLine("Found " + Mails.Count() + " mails...");

            if (ToMarkAsRead)
            {
                foreach (MailMessage mail in Mails)
                {
                    EmailMessage message = EmailMessage.Bind(service, mail.Headers["UID"], new PropertySet(BasePropertySet.FirstClassProperties, EmailMessageSchema.IsRead));
                    message.IsRead = true;
                    message.Update(ConflictResolutionMode.AutoResolve);
                }
            }

            return (Mails, service);
        }

        internal class MailMessageConverter
        {
            private readonly MailMessage _mailMessage;

            public MailMessageConverter()
            {
                this._mailMessage = new MailMessage()
                {
                    Priority = MailPriority.Normal
                };
            }

            public MailMessageConverter(MailMessage mailMessage)
            {
                MailMessage mailMessage1 = mailMessage ?? new MailMessage()
                {
                    Priority = MailPriority.Normal
                };
                this._mailMessage = mailMessage1;
            }

            private void AddRecipients(MailAddressCollection mailCollection, EmailAddressCollection exchangeMailCollection, ref NLog.Logger log, bool consoleLogging = false)
            {
                if (exchangeMailCollection == null)
                {
                    return;
                }
                foreach (Microsoft.Exchange.WebServices.Data.EmailAddress emailAddress in exchangeMailCollection)
                {
                    try
                    {
                        mailCollection.Add(new MailAddress(emailAddress.Address, emailAddress.Name));
                    }
                    catch
                    {

                        try
                        {
                            mailCollection.Add(new MailAddress(Functions.ExtractMailAddressesFromString(emailAddress.Address)[0], emailAddress.Name));
                        }
                        catch (Exception exception)
                        {
                            log.Error("Invalid recipient with name " + emailAddress.Name + " and address " + emailAddress.Address + ": " + exception.Message);
                            if (consoleLogging)
                                Console.WriteLine("Invalid recipient with name " + emailAddress.Name + " and address " + emailAddress.Address + ": " + exception.Message);
                        }
                    }

                }
                
            }

            public MailMessage Build()
            {
                return this._mailMessage;
            }

            public MailMessageConverter WithAttachements(Microsoft.Exchange.WebServices.Data.AttachmentCollection mimeAttachments)
            {
                try
                {
                    if (mimeAttachments.Count > 0)
                    {
                        foreach (Microsoft.Exchange.WebServices.Data.Attachment mimeAttachment in mimeAttachments)
                        {
                            try
                            {
                                System.Net.Mail.Attachment attachment = null;
                                string name = null;
                                Microsoft.Exchange.WebServices.Data.FileAttachment fileAttachment = mimeAttachment as Microsoft.Exchange.WebServices.Data.FileAttachment;
                                if (fileAttachment != null)
                                {
                                    fileAttachment.Load();
                                    if (!string.IsNullOrEmpty(fileAttachment.Name) && fileAttachment.Content != null)
                                    {
                                        attachment = new System.Net.Mail.Attachment(new MemoryStream(fileAttachment.Content), fileAttachment.Name, fileAttachment.ContentType);
                                        name = fileAttachment.Name;
                                    }
                                }
                                Microsoft.Exchange.WebServices.Data.ItemAttachment itemAttachment = mimeAttachment as Microsoft.Exchange.WebServices.Data.ItemAttachment;
                                if (itemAttachment != null)
                                {
                                    itemAttachment.Load(new PropertySet(new PropertyDefinitionBase[] { ItemSchema.MimeContent }));
                                    if (!string.IsNullOrEmpty(itemAttachment.Name) && itemAttachment.Item.MimeContent != null)
                                    {
                                        MemoryStream memoryStream = new MemoryStream(itemAttachment.Item.MimeContent.Content);
                                        name = string.Concat(itemAttachment.Name, ".eml");
                                        attachment = new System.Net.Mail.Attachment(memoryStream, name, itemAttachment.ContentType);
                                    }
                                }
                                if (attachment != null)
                                {
                                    attachment.Name = name;
                                    this._mailMessage.Attachments.Add(attachment);
                                }
                            }
                            catch (Exception exception)
                            {
                                Trace.TraceWarning(exception.ToString());
                            }
                        }
                    }
                }
                catch (Exception exception1)
                {
                    throw new Exception(exception1.Message);
                }
                return this;
            }

            public MailMessageConverter WithAttachements(List<string> files)
            {
                foreach (string file in files)
                {
                    this._mailMessage.Attachments.Add(new System.Net.Mail.Attachment(file));
                }
                return this;
            }

            public MailMessageConverter WithBccRecipients(EmailAddressCollection exchangeMailCollection, ref NLog.Logger log, bool consoleLogging = false)
            {
                this.AddRecipients(this._mailMessage.Bcc, exchangeMailCollection, ref log, consoleLogging);
                return this;
            }

            public MailMessageConverter WithBody(MessageBody body)
            {
                try
                {
                    this._mailMessage.IsBodyHtml = body.BodyType == 0;
                    this._mailMessage.Body = body.Text;
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(exception.ToString());
                }
                return this;
            }

            public MailMessageConverter WithBody(string body)
            {
                try
                {
                    this._mailMessage.Body = body;
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(exception.ToString());
                }
                return this;
            }

            public MailMessageConverter WithCCRecipients(EmailAddressCollection exchangeMailCollection, ref NLog.Logger log, bool consoleLogging = false)
            {
                this.AddRecipients(this._mailMessage.CC, exchangeMailCollection, ref log, consoleLogging);
                return this;
            }

            public MailMessageConverter WithFromAddress(string address, string name, bool consoleLogging = false)
            {
                if (address != null && name != null)
                {
                    this._mailMessage.From = new MailAddress(address, name);
                }
                return this;
            }

            public MailMessageConverter WithFromAddress(Microsoft.Exchange.WebServices.Data.EmailAddress from)
            {
                string address;
                string name;
                if (from != null)
                {
                    address = from.Address;
                }
                else
                {
                    address = null;
                }
                if (from != null)
                {
                    name = from.Name;
                }
                else
                {
                    name = null;
                }
                return this.WithFromAddress(address, name);
            }

            public MailMessageConverter WithHeader(string headerName, string headerValue)
            {
                if (!String.IsNullOrEmpty(headerName) && !String.IsNullOrEmpty(headerValue))
                    this._mailMessage.Headers[headerName] = headerValue;
                return this;
            }

            public MailMessageConverter WithHtmlBody(bool htmlBody)
            {
                this._mailMessage.IsBodyHtml = htmlBody;
                return this;
            }

            public MailMessageConverter WithPriority(Microsoft.Exchange.WebServices.Data.Importance mimeMessageImportance)
            {
                try
                {
                    if (mimeMessageImportance == Microsoft.Exchange.WebServices.Data.Importance.High)
                    {
                        this._mailMessage.Priority = MailPriority.High;
                    }
                    if (mimeMessageImportance == null)
                    {
                        this._mailMessage.Priority = MailPriority.Low;
                    }
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(exception.ToString());
                }
                return this;
            }

            public MailMessageConverter WithReplyToRecipients(EmailAddressCollection exchangeMailCollection, ref NLog.Logger log, bool consoleLogging = false)
            {
                this.AddRecipients(this._mailMessage.ReplyToList, exchangeMailCollection, ref log);
                return this;
            }

            public MailMessageConverter WithSender(string address, string name, ref NLog.Logger log, bool consoleLogging = true)
            {
                if (address != null && name != null)
                {
                    try
                    {
                        this._mailMessage.Sender = new MailAddress(address, name);
                    }
                    catch
                    {

                        try
                        {
                            this._mailMessage.Sender = new MailAddress(Functions.ExtractMailAddressesFromString(address)[0], name);
                        }
                        catch (Exception exception)
                        {
                            log.Error("Invalid sender with name " + name + " and address " + address + ": " + exception.Message);
                            if (consoleLogging)
                                Console.WriteLine("Invalid sender with name " + name + " and address " + address + ": " + exception.Message);
                            this._mailMessage.Sender = new MailAddress("SenderAddressError@mail.com", name);
                        }
                    }
                    
                }
                return this;
            }

            public MailMessageConverter WithSender(Microsoft.Exchange.WebServices.Data.EmailAddress sender, ref NLog.Logger log, bool consoleLogging = true)
            {
                string address;
                string name;
                if (sender != null)
                {
                    address = sender.Address;
                }
                else
                {
                    address = null;
                }
                if (sender != null)
                {
                    name = sender.Name;
                }
                else
                {
                    name = null;
                }
                return this.WithSender(address, name, ref log);
            }

            public MailMessageConverter WithSubject(string subject)
            {
                this._mailMessage.Subject = subject;
                return this;
            }

            public MailMessageConverter WithToRecipients(EmailAddressCollection exchangeMailCollection, ref NLog.Logger log, bool consoleLogging = false)
            {
                this.AddRecipients(this._mailMessage.To, exchangeMailCollection, ref log, consoleLogging);
                return this;
            }
        }
        public static MailMessage ConvertToMailMessage(EmailMessage exchangeMessage, ref NLog.Logger log, bool consoleLogging = false)
        {
            MailMessageConverter mailMessageConverter = new MailMessageConverter();
            mailMessageConverter.WithPriority(exchangeMessage.Importance);
            MailMessageConverter mailMessageConverter11 = mailMessageConverter.WithHeader("InternetUid", exchangeMessage.InternetMessageId);
            MailMessageConverter mailMessageConverter1 = mailMessageConverter11.WithHeader("Uid", exchangeMessage.Id.UniqueId);
            DateTime dateTimeCreated = exchangeMessage.DateTimeCreated;
            MailMessageConverter mailMessageConverter2 = mailMessageConverter1.WithHeader("DateCreated", dateTimeCreated.ToString());
            dateTimeCreated = exchangeMessage.DateTimeReceived;
            MailMessageConverter mailMessageConverter3 = mailMessageConverter2.WithHeader("DateReceived", dateTimeCreated.ToString());
            dateTimeCreated = exchangeMessage.DateTimeSent;
            MailMessageConverter mailMessageConverter4 = mailMessageConverter3.WithHeader("Date", dateTimeCreated.ToString());
            bool isRead = exchangeMessage.IsRead;
            mailMessageConverter4.WithHeader("IsRead", isRead.ToString());
            bool isMeetingCancellation = false;
            bool isMeetingRequest = false;
            bool isMeetingResponse = false;
            bool meetingIsRecurring = false;
            JObject meetingRecurrence = new JObject();
            DateTime? meetingStart = null;
            DateTime? meetingEnd = null;
            string meetingLocation = null;
            string meetingTimeZone = null;
            MeetingResponseType meetingResponseType = new MeetingResponseType();
            MailMessageConverter mailMessageConverter5 = mailMessageConverter4;
            try
            {
                MeetingResponse meetingResp = (MeetingResponse)exchangeMessage;
                isMeetingResponse = true;
                meetingResponseType = meetingResp.ResponseType;
                mailMessageConverter5 = mailMessageConverter4.WithHeader("MeetingResponseType", meetingResponseType.ToString()).WithHeader("AppointmentId",meetingResp.AssociatedAppointmentId.UniqueId);
            }
            catch
            {
                if (!isMeetingResponse)
                {
                    try
                    {
                        MeetingRequest meeting = (MeetingRequest)exchangeMessage;
                        isMeetingRequest = true;
                        log.Debug("Meeting request?: " + isMeetingRequest.ToString());
                        if (consoleLogging)
                            Console.WriteLine("Meeting request?: " + isMeetingRequest.ToString());
                        meetingIsRecurring = meeting.IsRecurring;
                        log.Debug("Meeting recurring?: " + meetingIsRecurring);
                        if (consoleLogging)
                            Console.WriteLine("Meeting recurring?: " + meetingIsRecurring);
                        meetingStart = meeting.Start;
                        meetingEnd = meeting.End;
                        meetingLocation = meeting.Location;
                        meetingTimeZone = meeting.TimeZone;
                        mailMessageConverter5 = mailMessageConverter4.WithHeader("MeetingStart", meeting.Start.ToString()).WithHeader("MeetingEnd", meeting.End.ToString()).WithHeader("MeetingLocation", meetingLocation).WithHeader("MeetingTimeZone", meetingTimeZone).WithHeader("MeetingIsRecurring", meetingIsRecurring.ToString()).WithHeader("AppointmentId", meeting.AssociatedAppointmentId.UniqueId).WithHeader("MeetingRequestType",meeting.MeetingRequestType.ToString());

                        if (meetingIsRecurring)
                        {
                            meetingRecurrence["pattern"] = meeting.Recurrence.ToString().Split('+')[1].Replace("Pattern", "");
                            meetingRecurrence["startDate"] = meeting.Recurrence.StartDate;
                            meetingRecurrence["hasEnd"] = meeting.Recurrence.HasEnd;
                            meetingRecurrence["endDate"] = meeting.Recurrence.EndDate;
                            meetingRecurrence["numOfOcc"] = meeting.Recurrence.NumberOfOccurrences;

                            switch (meetingRecurrence["pattern"].ToString())
                            {
                                case "Daily":
                                    meetingRecurrence["interval"] = ((Microsoft.Exchange.WebServices.Data.Recurrence.IntervalPattern.DailyPattern)meeting.Recurrence).Interval;
                                    break;
                                case "Weekly":
                                    meetingRecurrence["days"] = String.Join(",", ((Microsoft.Exchange.WebServices.Data.Recurrence.IntervalPattern.WeeklyPattern)meeting.Recurrence).DaysOfTheWeek.Select(x => x.ToString()));
                                    meetingRecurrence["interval"] = ((Microsoft.Exchange.WebServices.Data.Recurrence.IntervalPattern.WeeklyPattern)meeting.Recurrence).Interval;
                                    break;
                                case "Monthly":
                                    meetingRecurrence["day"] = ((Microsoft.Exchange.WebServices.Data.Recurrence.IntervalPattern.MonthlyPattern)meeting.Recurrence).DayOfMonth;
                                    meetingRecurrence["interval"] = ((Microsoft.Exchange.WebServices.Data.Recurrence.IntervalPattern.MonthlyPattern)meeting.Recurrence).Interval;
                                    break;
                                case "Yearly":
                                    meetingRecurrence["day"] = ((Microsoft.Exchange.WebServices.Data.Recurrence.IntervalPattern.YearlyPattern)meeting.Recurrence).DayOfMonth;
                                    meetingRecurrence["month"] = ((Microsoft.Exchange.WebServices.Data.Recurrence.IntervalPattern.YearlyPattern)meeting.Recurrence).Month.ToString();
                                    break;
                                default:
                                    break;
                            }
                            mailMessageConverter5 = mailMessageConverter5.WithHeader("MeetingRecurrence", Newtonsoft.Json.JsonConvert.SerializeObject(meetingRecurrence));
                            
                        }

                    }
                    catch
                    {
                        try
                        {
                            MeetingCancellation meetingCancellation = (MeetingCancellation)exchangeMessage;
                            isMeetingCancellation = true;
                            log.Debug("Meeting cancellation?: " + isMeetingCancellation.ToString());
                            if (consoleLogging)
                                Console.WriteLine("Meeting cancellation?: " + isMeetingCancellation.ToString());
                            mailMessageConverter5 = mailMessageConverter4.WithHeader("AppointmentId", meetingCancellation.AssociatedAppointmentId.UniqueId);

                        }
                        catch (Exception e)
                        {
                            log.Debug("Error casting meeting cancellation: " + e.Message);
                            if (consoleLogging)
                                Console.WriteLine("Error casting meeting cancellation: " + e.Message);
                        }
                    }
                }
            }
            
            MailMessageConverter mailMessageConverter6 = mailMessageConverter5.WithHeader("IsMeetingRequest", isMeetingRequest.ToString()).WithHeader("IsMeetingCancellation", isMeetingCancellation.ToString()).WithHeader("IsMeetingResponse", isMeetingResponse.ToString());
            mailMessageConverter.WithFromAddress(exchangeMessage.From).WithSender(exchangeMessage.Sender, ref log).WithSubject(exchangeMessage.Subject);
            mailMessageConverter.WithToRecipients(exchangeMessage.ToRecipients, ref log, consoleLogging).WithCCRecipients(exchangeMessage.CcRecipients, ref log, consoleLogging).WithBccRecipients(exchangeMessage.BccRecipients, ref log, consoleLogging).WithReplyToRecipients(exchangeMessage.ReplyTo, ref log, consoleLogging);
            mailMessageConverter.WithBody(exchangeMessage.Body);
            mailMessageConverter.WithAttachements(exchangeMessage.Attachments);
            return mailMessageConverter.Build();
        }

        /// <summary>
        /// Turn Exchange version in string to ExchangeVersion object
        /// </summary>
        /// <param name="version">string form of ExchangeVersion. Must be one of the following: 2007,2007SP1,2010,2010SP1,2010SP2,2013,2013SP1</param>
        /// <returns>ExchangeVersion variable</returns>
        public static ExchangeVersion StringToExchangeVersion(string version)
        {
            ExchangeVersion eversion = new ExchangeVersion();
            switch (version)
            {
                case "2007":
                    eversion = ExchangeVersion.Exchange2007_SP1;
                    break;
                case "2007SP1":
                    eversion = ExchangeVersion.Exchange2007_SP1;
                    break;
                case "2010":
                    eversion = ExchangeVersion.Exchange2010;
                    break;
                case "2010SP1":
                    eversion = ExchangeVersion.Exchange2010_SP1;
                    break;
                case "2010SP2":
                    eversion = ExchangeVersion.Exchange2010_SP2;
                    break;
                case "2013":
                    eversion = ExchangeVersion.Exchange2013;
                    break;
                case "2013SP1":
                    eversion = ExchangeVersion.Exchange2013_SP1;
                    break;
                default:
                    throw new System.Exception("Unknown version ("+version+")!");
            }
            return eversion;
        }
    }
}
