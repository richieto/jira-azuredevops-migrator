﻿using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.Client;
using WebApi = Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using WebModel = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Operations;
using VsWebApi = Microsoft.VisualStudio.Services.WebApi;
using Migration.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.VisualStudio.Services.Common;
using System.Net;
using Migration.WIContract;
using Migration.Common.Log;

namespace WorkItemImport
{
    public class Agent
    {
        private readonly MigrationContext _context;
        public Settings Settings { get; private set; }

        public TfsTeamProjectCollection Collection
        {
            get; private set;
        }

        private WorkItemStore _store;
        public WorkItemStore Store
        {
            get
            {
                if (_store == null)
                    _store = new WorkItemStore(Collection, WorkItemStoreFlags.BypassRules);

                return _store;
            }
        }

        public VsWebApi.VssConnection RestConnection { get; private set; }
        public Dictionary<string, int> IterationCache { get; private set; } = new Dictionary<string, int>();
        public int RootIteration { get; private set; }
        public Dictionary<string, int> AreaCache { get; private set; } = new Dictionary<string, int>();
        public int RootArea { get; private set; }

        private WebApi.WorkItemTrackingHttpClient _wiClient;
        public WebApi.WorkItemTrackingHttpClient WiClient
        {
            get
            {
                if (_wiClient == null)
                    _wiClient = RestConnection.GetClient<WebApi.WorkItemTrackingHttpClient>();

                return _wiClient;
            }
        }

        private Agent(MigrationContext context, Settings settings, VsWebApi.VssConnection restConn, TfsTeamProjectCollection soapConnection)
        {
            _context = context;
            Settings = settings;
            RestConnection = restConn;
            Collection = soapConnection;
        }

        #region Static
        internal static Agent Initialize(MigrationContext context, Settings settings)
        {
            var restConnection = EstablishRestConnection(settings);
            if (restConnection == null)
                return null;

            var soapConnection = EstablishSoapConnection(settings);
            if (soapConnection == null)
                return null;

            var agent = new Agent(context, settings, restConnection, soapConnection);

            // check if projects exists, if not create it
            var project = agent.GetOrCreateProjectAsync().Result;
            if (project == null)
            {
                Logger.Log(LogLevel.Critical, "Could not establish connection to the remote Azure DevOps/TFS project.");
                return null;
            }

            (var iterationCache, int rootIteration) = agent.CreateClasificationCacheAsync(settings.Project, WebModel.TreeStructureGroup.Iterations).Result;
            if (iterationCache == null)
            {
                Logger.Log(LogLevel.Critical, "Could not build iteration cache.");
                return null;
            }

            agent.IterationCache = iterationCache;
            agent.RootIteration = rootIteration;

            (var areaCache, int rootArea) = agent.CreateClasificationCacheAsync(settings.Project, WebModel.TreeStructureGroup.Areas).Result;
            if (areaCache == null)
            {
                Logger.Log(LogLevel.Critical, "Could not build area cache.");
                return null;
            }

            agent.AreaCache = areaCache;
            agent.RootArea = rootArea;

            return agent;
        }

        internal WorkItem CreateWI(string type)
        {
            var project = Store.Projects[Settings.Project];
            var wiType = project.WorkItemTypes[type];
            return wiType.NewWorkItem();
        }

        internal WorkItem GetWorkItem(int wiId)
        {
            return Store.GetWorkItem(wiId);
        }

        private static VsWebApi.VssConnection EstablishRestConnection(Settings settings)
        {
            try
            {
                Logger.Log(LogLevel.Info, "Connecting to Azure DevOps/TFS...");
                var credentials = new VssBasicCredential("", settings.Pat);
                var uri = new Uri(settings.Account);
                return new VsWebApi.VssConnection(uri, credentials);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Cannot establish connection to Azure DevOps/TFS.", LogLevel.Critical);
                return null;
            }
        }

        private static TfsTeamProjectCollection EstablishSoapConnection(Settings settings)
        {
            NetworkCredential netCred = new NetworkCredential(string.Empty, settings.Pat);
            VssBasicCredential basicCred = new VssBasicCredential(netCred);
            VssCredentials tfsCred = new VssCredentials(basicCred);
            var collection = new TfsTeamProjectCollection(new Uri(settings.Account), tfsCred);
            collection.Authenticate();
            return collection;
        }

        #endregion

        #region Setup

        internal async Task<TeamProject> GetOrCreateProjectAsync()
        {
            ProjectHttpClient projectClient = RestConnection.GetClient<ProjectHttpClient>();
            Logger.Log(LogLevel.Info, "Retreiving project info from Azure DevOps/TFS...");
            TeamProject project = null;

            try
            {
                project = await projectClient.GetProject(Settings.Project);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Failed to get Azure DevOps/TFS project '{Settings.Project}'.");
            }

            if (project == null)
                project = await CreateProject(Settings.Project, $"{Settings.ProcessTemplate} project for Jira migration", Settings.ProcessTemplate);

            return project;
        }

        internal async Task<TeamProject> CreateProject(string projectName, string projectDescription = "", string processName = "Scrum")
        {
            Logger.Log(LogLevel.Warning, $"Project '{projectName}' does not exist.");
            Console.WriteLine("Would you like to create one? (Y/N)");
            var answer = Console.ReadKey();
            if (answer.KeyChar != 'Y' && answer.KeyChar != 'y')
                return null;

            Logger.Log(LogLevel.Info, $"Creating project '{projectName}'.");

            // Setup version control properties
            Dictionary<string, string> versionControlProperties = new Dictionary<string, string>
            {
                [TeamProjectCapabilitiesConstants.VersionControlCapabilityAttributeName] = SourceControlTypes.Git.ToString()
            };

            // Setup process properties       
            ProcessHttpClient processClient = RestConnection.GetClient<ProcessHttpClient>();
            Guid processId = processClient.GetProcessesAsync().Result.Find(process => { return process.Name.Equals(processName, StringComparison.InvariantCultureIgnoreCase); }).Id;

            Dictionary<string, string> processProperaties = new Dictionary<string, string>
            {
                [TeamProjectCapabilitiesConstants.ProcessTemplateCapabilityTemplateTypeIdAttributeName] = processId.ToString()
            };

            // Construct capabilities dictionary
            Dictionary<string, Dictionary<string, string>> capabilities = new Dictionary<string, Dictionary<string, string>>
            {
                [TeamProjectCapabilitiesConstants.VersionControlCapabilityName] = versionControlProperties,
                [TeamProjectCapabilitiesConstants.ProcessTemplateCapabilityName] = processProperaties
            };

            // Construct object containing properties needed for creating the project
            TeamProject projectCreateParameters = new TeamProject()
            {
                Name = projectName,
                Description = projectDescription,
                Capabilities = capabilities
            };

            // Get a client
            ProjectHttpClient projectClient = RestConnection.GetClient<ProjectHttpClient>();

            TeamProject project = null;
            try
            {
                Logger.Log(LogLevel.Info, "Queuing project creation...");

                // Queue the project creation operation 
                // This returns an operation object that can be used to check the status of the creation
                OperationReference operation = await projectClient.QueueCreateProject(projectCreateParameters);

                // Check the operation status every 5 seconds (for up to 30 seconds)
                Operation completedOperation = WaitForLongRunningOperation(operation.Id, 5, 30).Result;

                // Check if the operation succeeded (the project was created) or failed
                if (completedOperation.Status == OperationStatus.Succeeded)
                {
                    // Get the full details about the newly created project
                    project = projectClient.GetProject(
                        projectCreateParameters.Name,
                        includeCapabilities: true,
                        includeHistory: true).Result;

                    Logger.Log(LogLevel.Info, $"Project created (ID: {project.Id})");
                }
                else
                {
                    Logger.Log(LogLevel.Error, "Project creation operation failed: " + completedOperation.ResultMessage);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Exception during create project.", LogLevel.Critical);
            }

            return project;
        }

        private async Task<Operation> WaitForLongRunningOperation(Guid operationId, int interavalInSec = 5, int maxTimeInSeconds = 60, CancellationToken cancellationToken = default(CancellationToken))
        {
            OperationsHttpClient operationsClient = RestConnection.GetClient<OperationsHttpClient>();
            DateTime expiration = DateTime.Now.AddSeconds(maxTimeInSeconds);
            int checkCount = 0;

            while (true)
            {
                Logger.Log(LogLevel.Info, $" Checking status ({checkCount++})... ");

                Operation operation = await operationsClient.GetOperation(operationId, cancellationToken);

                if (!operation.Completed)
                {
                    Logger.Log(LogLevel.Info, $"   Pausing {interavalInSec} seconds...");

                    await Task.Delay(interavalInSec * 1000);

                    if (DateTime.Now > expiration)
                    {
                        Logger.Log(LogLevel.Error, $"Operation did not complete in {maxTimeInSeconds} seconds.");
                    }
                }
                else
                {
                    return operation;
                }
            }
        }

        private async Task<(Dictionary<string, int>, int)> CreateClasificationCacheAsync(string project, WebModel.TreeStructureGroup structureGroup)
        {
            try
            {
                Logger.Log(LogLevel.Info, $"Building {(structureGroup == WebModel.TreeStructureGroup.Iterations ? "iteration" : "area")} cache...");
                WebModel.WorkItemClassificationNode all = await WiClient.GetClassificationNodeAsync(project, structureGroup, null, 1000);

                var clasificationCache = new Dictionary<string, int>();

                if (all.Children != null && all.Children.Any())
                {
                    foreach (var iteration in all.Children)
                        CreateClasificationCacheRec(iteration, clasificationCache, "");
                }

                return (clasificationCache, all.Id);
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Error while building {(structureGroup == WebModel.TreeStructureGroup.Iterations ? "iteration" : "area")} cache.");
                return (null, -1);
            }
        }

        private void CreateClasificationCacheRec(WebModel.WorkItemClassificationNode current, Dictionary<string, int> agg, string parentPath)
        {
            string fullName = !string.IsNullOrWhiteSpace(parentPath) ? parentPath + "/" + current.Name : current.Name;

            agg.Add(fullName, current.Id);
            Logger.Log(LogLevel.Debug, $"{(current.StructureType == WebModel.TreeNodeStructureType.Iteration ? "Iteration" : "Area")} '{fullName}' added to cache");
            if (current.Children != null)
            {
                foreach (var node in current.Children)
                    CreateClasificationCacheRec(node, agg, fullName);
            }
        }

        public int? EnsureClasification(string fullName, WebModel.TreeStructureGroup structureGroup = WebModel.TreeStructureGroup.Iterations)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                Logger.Log(LogLevel.Error, "Empty value provided for node name/path.");
                throw new ArgumentException("fullName");
            }

            var path = fullName.Split('/');
            var name = path.Last();
            var parent = string.Join("/", path.Take(path.Length - 1));

            if (!string.IsNullOrEmpty(parent))
                EnsureClasification(parent, structureGroup);

            var cache = structureGroup == WebModel.TreeStructureGroup.Iterations ? IterationCache : AreaCache;

            lock (cache)
            {
                if (cache.TryGetValue(fullName, out int id))
                    return id;

                WebModel.WorkItemClassificationNode node = null;

                try
                {
                    node = WiClient.CreateOrUpdateClassificationNodeAsync(
                        new WebModel.WorkItemClassificationNode() { Name = name, }, Settings.Project, structureGroup, parent).Result;
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Error while adding {(structureGroup == WebModel.TreeStructureGroup.Iterations ? "iteration" : "area")} '{fullName}' to Azure DevOps/TFS.", LogLevel.Critical);
                }

                if (node != null)
                {
                    Logger.Log(LogLevel.Debug, $"{(structureGroup == WebModel.TreeStructureGroup.Iterations ? "Iteration" : "Area")} '{fullName}' added to Azure DevOps/TFS.");
                    cache.Add(fullName, node.Id);
                    Store.RefreshCache();
                    return node.Id;
                }
            }
            return null;
        }

        #endregion

        #region Import Revision

        private bool UpdateWIFields(List<WiField> fields, WorkItem wi)
        {
            bool success = true;

            if (!wi.IsOpen || !wi.IsPartialOpen)
                wi.PartialOpen();

            foreach (var fieldRev in fields)
            {
                try
                {
                    string fieldRef = fieldRev.ReferenceName;
                    object fieldValue = fieldRev.Value;

                    if (fieldRef.Equals("System.IterationPath", StringComparison.InvariantCultureIgnoreCase))
                    {
                        string iterationPath = Settings.BaseIterationPath;

                        if (!string.IsNullOrWhiteSpace((string)fieldValue))
                        {
                            if (string.IsNullOrWhiteSpace(iterationPath))
                                iterationPath = (string)fieldValue;
                            else
                                iterationPath = string.Join("/", iterationPath, (string)fieldValue);
                        }

                        if (!string.IsNullOrWhiteSpace(iterationPath))
                        {
                            EnsureClasification(iterationPath, WebModel.TreeStructureGroup.Iterations);
                            wi.IterationPath = $@"{Settings.Project}\{iterationPath}".Replace("/", @"\");
                        }
                        else
                        {
                            wi.IterationPath = Settings.Project;
                        }

                        Logger.Log(LogLevel.Debug, $"Mapped IterationPath '{wi.IterationPath}'.");
                    }
                    else if (fieldRef.Equals("System.AreaPath", StringComparison.InvariantCultureIgnoreCase))
                    {
                        string areaPath = Settings.BaseAreaPath;

                        if (!string.IsNullOrWhiteSpace((string)fieldValue))
                        {
                            if (string.IsNullOrWhiteSpace(areaPath))
                                areaPath = (string)fieldValue;
                            else
                                areaPath = string.Join("/", areaPath, (string)fieldValue);
                        }

                        if (!string.IsNullOrWhiteSpace(areaPath))
                        {
                            EnsureClasification(areaPath, WebModel.TreeStructureGroup.Areas);
                            wi.AreaPath = $@"{Settings.Project}\{areaPath}".Replace("/", @"\");
                        }
                        else
                        {
                            wi.AreaPath = Settings.Project;
                        }

                        Logger.Log(LogLevel.Debug, $"Mapped AreaPath '{wi.AreaPath}'.");
                    }
                    else
                    {
                        if (fieldValue != null)
                        {
                            var field = wi.Fields[fieldRef];
                            field.Value = fieldValue;

                            Logger.Log(LogLevel.Debug, $"Mapped '{fieldRef}' '{fieldValue}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Failed to update fields.");
                    success = false;
                }
            }

            return success;
        }

        private bool ApplyAttachments(WiRevision rev, WorkItem wi, Dictionary<string, Attachment> attachmentMap)
        {
            bool success = true;

            if (!wi.IsOpen)
                wi.Open();

            foreach (var att in rev.Attachments)
            {
                try
                {
                    Logger.Log(LogLevel.Debug, $"Adding attachment '{att.ToString()}'.");
                    if (att.Change == ReferenceChangeType.Added)
                    {
                        var newAttachment = new Attachment(att.FilePath, att.Comment);
                        wi.Attachments.Add(newAttachment);

                        attachmentMap.Add(att.AttOriginId, newAttachment);
                    }
                    else if (att.Change == ReferenceChangeType.Removed)
                    {
                        Attachment existingAttachment = IdentifyAttachment(att, wi);
                        if (existingAttachment != null)
                        {
                            wi.Attachments.Remove(existingAttachment);
                        }
                        else
                        {
                            success = false;
                            Logger.Log(LogLevel.Error, $"Could not find migrated attachment '{att.ToString()}'.");
                        }
                    }
                }
                catch (AbortMigrationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Failed to apply attachments for '{wi.Id}'.");
                    success = false;
                }
            }

            if (rev.Attachments.Any(a => a.Change == ReferenceChangeType.Removed))
                wi.Fields[CoreField.History].Value = $"Removed attachments(s): { string.Join(";", rev.Attachments.Where(a => a.Change == ReferenceChangeType.Removed).Select(a => a.ToString()))}";

            return success;
        }

        private Attachment IdentifyAttachment(WiAttachment att, WorkItem wi)
        {
            if (_context.Journal.IsAttachmentMigrated(att.AttOriginId, out int attWiId))
                return wi.Attachments.Cast<Attachment>().SingleOrDefault(a => a.Id == attWiId);
            return null;
        }

        private bool ApplyLinks(WiRevision rev, WorkItem wi)
        {
            bool success = true;

            if (!wi.IsOpen)
                wi.Open();


            foreach (var link in rev.Links)
            {
                try
                {
                    int sourceWiId = _context.Journal.GetMigratedId(link.SourceOriginId);
                    int targetWiId = _context.Journal.GetMigratedId(link.TargetOriginId);

                    link.SourceWiId = sourceWiId;
                    link.TargetWiId = targetWiId;

                    if (link.TargetWiId == -1)
                    {
                        var errorLevel = Settings.IgnoreFailedLinks ? LogLevel.Warning : LogLevel.Error;
                        Logger.Log(errorLevel, $"'{link.ToString()}' - target work item for Jira '{link.TargetOriginId}' is not yet created in Azure DevOps/TFS.");
                        success = false;
                        continue;
                    }

                    if (link.Change == ReferenceChangeType.Added && !AddLink(link, wi))
                    {
                        success = false;
                    }
                    else if (link.Change == ReferenceChangeType.Removed && !RemoveLink(link, wi))
                    {
                        success = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Failed to apply links for '{wi.Id}'.");
                    success = false;
                }
            }

            if (rev.Links.Any(l => l.Change == ReferenceChangeType.Removed))
                wi.Fields[CoreField.History].Value = $"Removed link(s): { string.Join(";", rev.Links.Where(l => l.Change == ReferenceChangeType.Removed).Select(l => l.ToString()))}";
            else if (rev.Links.Any(l => l.Change == ReferenceChangeType.Added))
                wi.Fields[CoreField.History].Value = $"Added link(s): { string.Join(";", rev.Links.Where(l => l.Change == ReferenceChangeType.Added).Select(l => l.ToString()))}";

            return success;
        }

        private bool AddLink(WiLink link, WorkItem wi)
        {
            var linkEnd = ParseLinkEnd(link, wi);

            if (linkEnd != null)
            {
                var relatedLink = new RelatedLink(linkEnd, link.TargetWiId);
                relatedLink = ResolveCiclycalLinks(relatedLink, wi);
                wi.Links.Add(relatedLink);
                return true;
            }
            else
                return false;

        }

        private RelatedLink ResolveCiclycalLinks(RelatedLink link, WorkItem wi)
        {
            if (link.LinkTypeEnd.LinkType.IsNonCircular && DetectCycle(wi, link))
                return new RelatedLink(link.LinkTypeEnd.OppositeEnd, link.RelatedWorkItemId);

            return link;
        }

        private bool DetectCycle(WorkItem startingWi, RelatedLink startingLink)
        {
            WorkItem nextWi;
            var nextWiLink = startingLink;

            do
            {
                nextWi = Store.GetWorkItem(nextWiLink.RelatedWorkItemId);
                nextWiLink = nextWi.Links.OfType<RelatedLink>().FirstOrDefault(rl => rl.LinkTypeEnd.Id == startingLink.LinkTypeEnd.Id);

                if (nextWiLink != null && nextWiLink.RelatedWorkItemId == startingWi.Id)
                    return true;

            } while (nextWiLink != null);

            return false;
        }

        private WorkItemLinkTypeEnd ParseLinkEnd(WiLink link, WorkItem wi)
        {
            var props = link.WiType.Split('-');
            var linkType = wi.Project.Store.WorkItemLinkTypes.SingleOrDefault(lt => lt.ReferenceName == props[0]);
            if (linkType == null)
            {
                Logger.Log(LogLevel.Error, $"'{link.ToString()}' - link type ({props[0]}) does not exist in project");
                return null;
            }

            WorkItemLinkTypeEnd linkEnd = null;

            if (linkType.IsDirectional)
            {
                if (props.Length > 1)
                    linkEnd = props[1] == "Forward" ? linkType.ForwardEnd : linkType.ReverseEnd;
                else
                    Logger.Log(LogLevel.Error, $"'{link.ToString()}' - link direction not provided for '{wi.Id}'.");
            }
            else
                linkEnd = linkType.ForwardEnd;

            return linkEnd;
        }

        private bool RemoveLink(WiLink link, WorkItem wi)
        {
            var linkToRemove = wi.Links.OfType<RelatedLink>().SingleOrDefault(rl => rl.LinkTypeEnd.ImmutableName == link.WiType && rl.RelatedWorkItemId == link.TargetWiId);
            if (linkToRemove == null)
            {
                Logger.Log(LogLevel.Warning, $"{link.ToString()} - cannot identify link to remove for '{wi.Id}'.");
                return false;
            }
            wi.Links.Remove(linkToRemove);
            return true;
        }

        private void SaveWorkItem(WiRevision rev, WorkItem newWorkItem)
        {
            if (!newWorkItem.IsValid())
            {
                var reasons = newWorkItem.Validate();
                foreach (Microsoft.TeamFoundation.WorkItemTracking.Client.Field reason in reasons)
                    Logger.Log(LogLevel.Info, $"Field: '{reason.Name}', Status: '{reason.Status}', Value: '{reason.Value}'");
            }
            try
            {
                newWorkItem.Save(SaveFlags.MergeAll);
            }
            catch (FileAttachmentException faex)
            {
                Logger.Log(faex, $"[{faex.GetType().ToString()}] {faex.Message}. Attachment {faex.SourceAttachment.Name}({faex.SourceAttachment.Id}) in {rev.ToString()} will be skipped.");
                newWorkItem.Attachments.Remove(faex.SourceAttachment);
                SaveWorkItem(rev, newWorkItem);
            }
        }

        private void EnsureAuthorFields(WiRevision rev)
        {
            string changedByRef = "System.ChangedBy";
            string createdByRef = "System.CreatedBy";
            if (rev.Index == 0 && !rev.Fields.Any(f => f.ReferenceName.Equals(createdByRef, StringComparison.InvariantCultureIgnoreCase)))
            {
                rev.Fields.Add(new WiField() { ReferenceName = createdByRef, Value = rev.Author });
            }
            if (!rev.Fields.Any(f => f.ReferenceName.Equals(changedByRef, StringComparison.InvariantCultureIgnoreCase)))
            {
                rev.Fields.Add(new WiField() { ReferenceName = changedByRef, Value = rev.Author });
            }
        }

        private void EnsureAssigneeField(WiRevision rev, WorkItem wi)
        {
            string assignedToRef = "System.AssignedTo";
            string assignedTo = wi.Fields[assignedToRef].Value.ToString();
            
            if (rev.Fields.Any(f => f.ReferenceName.Equals(assignedToRef, StringComparison.InvariantCultureIgnoreCase)))
            {
                var field =  rev.Fields.Where(f=>f.ReferenceName.Equals(assignedToRef, StringComparison.InvariantCultureIgnoreCase)).First();
                assignedTo =field.Value.ToString();
                rev.Fields.RemoveAll(f => f.ReferenceName.Equals(assignedToRef, StringComparison.InvariantCultureIgnoreCase));
            }
            rev.Fields.Add(new WiField() { ReferenceName = assignedToRef, Value = assignedTo });
        }
        
        private void EnsureDateFields(WiRevision rev)
        {
            string changedDateRef = "System.ChangedDate";
            string createdDateRef = "System.CreatedDate";
            if (rev.Index == 0 && !rev.Fields.Any(f => f.ReferenceName.Equals(createdDateRef, StringComparison.InvariantCultureIgnoreCase)))
            {
                rev.Fields.Add(new WiField() { ReferenceName = createdDateRef, Value = rev.Time.ToString("o") });
            }
            if (!rev.Fields.Any(f => f.ReferenceName.Equals(changedDateRef, StringComparison.InvariantCultureIgnoreCase)))
            {
                rev.Fields.Add(new WiField() { ReferenceName = changedDateRef, Value = rev.Time.ToString("o") });
            }
        }

        private bool CorrectDescription(WorkItem wi, WiItem wiItem, WiRevision rev)
        {
            string currentDescription = wi.Type.Name == "Bug" ? wi.Fields["Microsoft.VSTS.TCM.ReproSteps"].Value.ToString() : wi.Description;
            if (string.IsNullOrWhiteSpace(currentDescription))
                return false;

            bool descUpdated = false;
            foreach (var att in wiItem.Revisions.SelectMany(r => r.Attachments.Where(a => a.Change == ReferenceChangeType.Added)))
            {
                if (currentDescription.Contains(att.FilePath))
                {
                    var tfsAtt = IdentifyAttachment(att, wi);
                    descUpdated = true;

                    if (tfsAtt != null)
                    {
                        currentDescription = currentDescription.Replace(att.FilePath, tfsAtt.Uri.AbsoluteUri);
                        descUpdated = true;
                    }
                    else
                        Logger.Log(LogLevel.Warning, $"Attachment '{att.ToString()}' referenced in description but is missing from work item {wiItem.OriginId}/{wi.Id}.");
                }
            }

            if (descUpdated)
            {
                DateTime changedDate;
                if (wiItem.Revisions.Count > rev.Index + 1)
                    changedDate = RevisionUtility.NextValidDeltaRev(rev.Time, wiItem.Revisions[rev.Index + 1].Time);
                else
                    changedDate = RevisionUtility.NextValidDeltaRev(rev.Time);

                wi.Fields["System.ChangedDate"].Value = changedDate;
                wi.Fields["System.ChangedBy"].Value = rev.Author;

                if (wi.Type.Name == "Bug")
                {
                    wi.Fields["Microsoft.VSTS.TCM.ReproSteps"].Value = currentDescription;
                }
                else
                {
                    wi.Fields["System.Description"].Value = currentDescription;
                }
            }

            return descUpdated;
        }

        public bool ImportRevision(WiRevision rev, WorkItem wi)
        {
            try
            {
                bool incomplete = false;

                if (rev.Index == 0)
                    EnsureClasificationFields(rev);

                EnsureDateFields(rev);
                EnsureAuthorFields(rev);
                EnsureAssigneeField(rev,wi);

                var attachmentMap = new Dictionary<string, Attachment>();
                if (rev.Attachments.Any() && !ApplyAttachments(rev, wi, attachmentMap))
                    incomplete = true;

                if (rev.Fields.Any() && !UpdateWIFields(rev.Fields, wi))
                    incomplete = true;

                if (rev.Links.Any() && !ApplyLinks(rev, wi))
                    incomplete = true;

                if (incomplete)
                    Logger.Log(LogLevel.Error, $"'{rev.ToString()}' - not all changes were saved.");

                if (!rev.Attachments.Any(a => a.Change == ReferenceChangeType.Added) && rev.AttachmentReferences)
                {
                    Logger.Log(LogLevel.Debug, $"Correcting description on '{rev.ToString()}'.");
                    CorrectDescription(wi, _context.GetItem(rev.ParentOriginId), rev);
                }

                SaveWorkItem(rev, wi);

                if (rev.Attachments.Any(a => a.Change == ReferenceChangeType.Added) && rev.AttachmentReferences)
                {
                    Logger.Log(LogLevel.Debug, $"Correcting description on separate revision on '{rev.ToString()}'.");

                    try
                    {
                        if (CorrectDescription(wi, _context.GetItem(rev.ParentOriginId), rev))
                            SaveWorkItem(rev, wi);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(ex, $"Failed to correct description for '{wi.Id}', rev '{rev.ToString()}'.");
                    }
                }

                _context.Journal.MarkRevProcessed(rev.ParentOriginId, wi.Id, rev.Index);
                foreach (var wiAtt in rev.Attachments)
                {
                    if (attachmentMap.TryGetValue(wiAtt.AttOriginId, out Attachment tfsAtt) && tfsAtt.IsSaved)
                        _context.Journal.MarkAttachmentAsProcessed(wiAtt.AttOriginId, tfsAtt.Id);
                }

                Logger.Log(LogLevel.Debug, $"Imported revision.");

                wi.Close();

                return true;
            }
            catch (AbortMigrationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"Failed to import revisions for '{wi.Id}'.");
                return false;
            }
        }

        private void EnsureClasificationFields(Migration.WIContract.WiRevision rev)
        {
            if (!rev.Fields.Any(f => f.ReferenceName == "System.AreaPath"))
                rev.Fields.Add(new WiField() { ReferenceName = "System.AreaPath", Value = "" });

            if (!rev.Fields.Any(f => f.ReferenceName == "System.IterationPath"))
                rev.Fields.Add(new WiField() { ReferenceName = "System.IterationPath", Value = "" });
        }

        #endregion
    }
}