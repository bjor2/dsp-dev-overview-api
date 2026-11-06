"""Generates ADDENDA-011 layer JSON under dev-overview only. Reads (never writes) dsp-customerproject / dsp-price.
Every repo citation is hashed from the file bytes at generation time."""
import hashlib, json
from pathlib import Path
DSP = Path('/workspace/dsp'); BASE = DSP / 'dev-overview/architecture'
REVIEWED = '2026-11-06T11:30:00+01:00'

def sha(rel): return 'sha256:' + hashlib.sha256((DSP / rel).read_bytes()).hexdigest()
def R(rel, note, conf='confirmed'):
    assert (DSP / rel).is_file(), rel
    return {'kind': 'repo', 'ref': rel, 'note': note, 'confidence': conf, 'revision': sha(rel)}
def REG(proj, note):
    rel = f'dev-overview/architecture/registrations/{proj}.dev.json'
    return {'kind': 'registration', 'ref': rel, 'note': note, 'confidence': 'confirmed', 'revision': sha(rel)}
def CONF(pid, ver, note):
    return {'kind': 'confluence', 'ref': f'https://dahl-it.atlassian.net/wiki/spaces/DSPDOC/pages/{pid}', 'revision': f'version:{ver}', 'confidence': 'confirmed', 'note': note}
def N(id, label, kind, resp, boundary, srcs, unc=(), **extra):
    d = {'id': id, 'label': label, 'kind': kind, 'responsibility': resp, 'boundary': boundary, 'sources': srcs, 'uncertainties': list(unc)}
    d.update(extra); return d
def E(id, f, t, rel, label, desc, srcs, unc=()):
    return {'id': id, 'from': f, 'to': t, 'kind': 'conceptual', 'relationship': rel, 'label': label, 'description': desc, 'sources': srcs, 'uncertainties': list(unc)}
def L(id, project, title, summary, parent, flow, sources, unc, nodes, edges):
    d = {'$schema': '../../architecture-layer.schema.json', 'schemaVersion': 1, 'documentType': 'architecture-layer', 'id': id, 'project': project,
         'title': title, 'summary': summary, 'parentLayerId': parent}
    if flow: d['flowRef'] = {'containerId': flow}
    d.update({'scope': 'both', 'reviewedAt': REVIEWED, 'sources': sources, 'uncertainties': unc, 'nodes': nodes, 'edges': edges}); return d

CP = 'dsp-customerproject/src/'
c = lambda p, note: R(CP + p, note)
# ---- customerproject evidence ----
DOMH = c('Dahl.CustomerProject.Functions/Functions/CustomerProjectDomainQueueHandler.cs', 'Lines 31\u201356: DOMAIN trigger; UpdateCustomerAddressFromSnapshot and UpdateProjectWithPriceFromSnapshot subjects; unknown subjects are logged and discarded.')
CMDH = c('Dahl.CustomerProject.Functions/Functions/CustomerProjectQueueHandler.cs', 'Lines 129\u2013257: COMMANDS trigger; 14 subject cases (upsert, order validation/notification, imports, project creation/sync, order-reference events); unknown subjects throw.')
FPROG = c('Dahl.CustomerProject.Functions/Program.cs', 'Lines 67\u201380: Functions host registers messaging, domain queue client, storage, ERP, OrderHistory, Product, notification sender, application features and image storage.')
APPDI = c('Dahl.CustomerProject.Application/Configuration/ServiceCollectionExtensions.cs', 'Lines 17\u201349: AddApplicationFeatures (Functions) vs AddApiApplicationFeatures (API); CustomerProjectService and ManualAssociationService are registered only for the API.')
UCA = c('Dahl.CustomerProject.Application/Features/UpdateFromIngressSnapshot/UpdateCustomerAddressFromSnapshotHandler.cs', 'Lines 41\u201371: save address from snapshot; new Delivery address -> CreateProjectsFromErpReferences, modified address -> HandleCustomerAddressChanged.')
UCA_FLAG = c('Dahl.CustomerProject.Application/Features/UpdateFromIngressSnapshot/UpdateCustomerAddressFromSnapshotHandler.cs', 'Lines 43\u201345 and 73\u201377: discarded saves stop unless feature EnableM3CloudEventSendingOnNoChange applies.')
UCA_KEY = c('Dahl.CustomerProject.Application/Features/UpdateFromIngressSnapshot/UpdateCustomerAddressFromSnapshotHandler.cs', 'Lines 21\u201336 and 81\u2013106: Dahl.Core ImportStorableEntityFromSnapshotBase with bookkeeping + storable-entity storage; composite key lookup.')
UPP = c('Dahl.CustomerProject.Application/Features/UpdateFromIngressSnapshot/UpdateProjectWithPriceFromSnapshotHandler.cs', 'Lines 16\u201334: save ProjectWithPrice from snapshot; comment states no CustomerProjectChanged publish is needed.')
CFE = c('Dahl.CustomerProject.Application/Features/Upsert/CustomerProject/CreateFromErpReferenceHandler.cs', 'Lines 35\u201389: create project per ErpReference if missing, publish CustomerProjectChanged, requeue PopulateOrderReferences.')
CAC = c('Dahl.CustomerProject.Application/Features/Upsert/CustomerProject/CustomerAddressChangedHandler.cs', 'Lines 34\u201369: sync project dates from address, requeue PopulateOrderReferences when project becomes active, publish CustomerProjectChanged.')
CMP = c('Dahl.CustomerProject.Application/Features/Upsert/CustomerProject/CreateMissingProjectsHandler.cs', 'Lines 33\u201364: find addresses without project and requeue CreateProjectsFromErpReferences batches.')
SPD = c('Dahl.CustomerProject.Application/Features/Upsert/CustomerProject/SyncProjectDatesHandler.cs', 'Lines 31\u201355: find projects whose dates differ from address and requeue HandleCustomerAddressChanged.')
UCAH = c('Dahl.CustomerProject.Application/Features/Upsert/CustomerAddress/UpsertCustomerAddressHandler.cs', 'Lines 28\u201362: validate, create/upsert address in ERP, then upsert locally.')
ERPS = c('Dahl.CustomerProject.Application/Features/Upsert/ErpAddress/ErpAddressService.cs', 'Lines 20\u201372: candidate AddressId calculation, ERP create/update via IErpCustomerAddressClient.')
ERPC = c('Dahl.CustomerProject.Integrations.Erp/ErpCustomerAddressClient.cs', 'Lines 29\u2013126: ERP customer-address create, existing-id lookup and update calls.')
IOR = c('Dahl.CustomerProject.Application/Features/OrderAssociation/ImportOrderReferences.cs', 'Lines 35\u201360 and 60\u2013184: requeue PopulateOrderReferences per project; fetch OrderHistory, upsert OrderReference, publish CustomerProjectOrderReferenceChanged.')
OAC = c('Dahl.CustomerProject.Application/Features/OrderAssociation/OrderAddressIdChangedHandler.cs', 'Lines 32\u2013135: re-associate order on address change; upsert OrderReference; publish CustomerProjectOrderReferenceChanged.')
VOH = c('Dahl.CustomerProject.Application/Features/Notification/ValidateOrderHandler.cs', 'Lines 34\u201390: look up project by order, validate for notification, requeue SendNotification (email now, SMS scheduled).')
PVS = c('Dahl.CustomerProject.Application/Features/Notification/ProjectOrderValidatorService.cs', 'Lines 26\u201344: read OrderHistory order and Product catalog products.')
ORC = c('Dahl.CustomerProject.Application/Features/Notification/OrderReferenceChangedHandler.cs', 'Lines 21\u201326: CustomerProjectOrderReferenceChanged -> requeue ValidateOrder.')
ODH = c('Dahl.CustomerProject.Application/Features/Notification/OrderDeliveredHandler.cs', 'Lines 25\u201334: OrderDelivered -> requeue ValidateOrder.')
SNH = c('Dahl.CustomerProject.Application/Features/Notification/SendNotificationHandler.cs', 'Lines 20\u201322: SendNotification -> INotificationSender.')
NSND = c('Dahl.CustomerProject.Integrations.Communication/NotificationSender.cs', 'Lines 39\u201364: choose email/SMS message factory and send via communication queue client.')
CQC = c('Dahl.CustomerProject.Integrations.Communication/Client/CommunicationQueueClient.cs', 'Lines 14\u201343: sender bound to ServiceBusCommunicationQueue setting.')
OHC = c('Dahl.CustomerProject.Integrations.OrderHistory/OrderHistoryClient.cs', 'Lines 13\u201369: HTTP OrderHistory client (by order id, or by customer + ERP address).')
PRC = c('Dahl.CustomerProject.Integrations.Product/ProductClient.cs', 'Lines 12\u201323: HTTP product catalog client.')
IPIS = c('Dahl.CustomerProject.Application/Features/Images/ImportProjectImageService.cs', 'Lines 21\u201333: ImportImage command -> image service upload.')
IPS = c('Dahl.CustomerProject.Application/Features/Images/Migration/ImportProjectService.cs', 'Lines 37\u2013132: ImportCustomerProjects / ImportProjectContactsList migration import into storage.')
CPIS = c('Dahl.CustomerProject.Application/Features/Images/CustomerProjectImageService.cs', 'Lines 18\u201375: read/upload/remove project images: blob via IImageStorage, metadata via storage.')
IMGS = c('Dahl.CustomerProject.Images/ImageStorage.cs', 'Lines 18\u201351: processes (checksum/metadata strip) and uploads/removes blobs.')
BLOB = c('Dahl.CustomerProject.Images/BlobStorage/BlobStorageClient.cs', 'Lines 8\u201376: Azure Blob container client download/upload/delete.')
STOR = c('Dahl.CustomerProject.Storage/CustomerProjectStorageDbContext.cs', 'EF DbContext for CustomerProject SQL storage; no connection values copied.')
TOPIC = c('Dahl.CustomerProject.Messaging/CustomerProjectTopicClient.cs', 'Lines 16\u201322: topic sender configured by CustomerProjectTopic.')
QCL = c('Dahl.CustomerProject.Messaging/CustomerProjectQueueClient.cs', 'Lines 14\u201318: queue sender configured by CustomerProjectQueue.')
MIG_SNAP = c('Dahl.CustomerProject.Storage/Migrations/20240529083503_SnapshotBookkeeping.cs', 'Migration adding snapshot bookkeeping storage used by snapshot destinations.')
MIG_DEST = c('Dahl.CustomerProject.Storage/Migrations/20240528125402_CustomerAddressAndProjectWithPriceAsSnapshotDestinations.cs', 'Migration making CustomerAddress and ProjectWithPrice snapshot destinations.')
CPC = c('Dahl.CustomerProject.Api/V1/Controllers/CustomerProjectController.cs', 'Lines 22\u2013306: tenant-scoped project reads/search, create (POST) and update (PUT) via ICustomerProjectService, and admin/import POSTs that only enqueue commands.')
OAC_CTRL = c('Dahl.CustomerProject.Api/V1/Controllers/OrderAssociationController.cs', 'Lines 20\u2013119: manual associate/remove via IManualAssociationService; populate enqueues PopulateOrderReferences.')
IMG_CTRL = c('Dahl.CustomerProject.Api/V1/Controllers/ImageController.cs', 'Lines 20\u2013181: image read/list/upload/delete via IImageService; send-import-command enqueues ImportImage.')
ENV_CTRL = c('Dahl.CustomerProject.Api/V1/Controllers/EnvironmentalProfileController.cs', 'Lines 15\u201384: environmental profile upsert/list/delete directly against storage.')
CPS = c('Dahl.CustomerProject.Application/Features/Upsert/CustomerProject/CustomerProjectService.cs', 'Lines 37\u2013227: validate, create/update ERP address, upsert project, publish CustomerProjectChanged, enqueue PopulateOrderReferences.')
MAS = c('Dahl.CustomerProject.Application/Features/OrderAssociation/ManualAssociationService.cs', 'Lines 23\u2013126: manual order association/removal, OrderReference upsert, publish CustomerProjectOrderReferenceChanged.')
APROG = c('Dahl.CustomerProject.Api/Program.cs', 'Lines 39\u201372: Okta authentication, storage, messaging, ingress queue client, ERP integration, image storage, API application features.')
CPREG = lambda note: REG('customerproject', note)
C513 = CONF('22779513', 4, '2023 workshop domain context (project vs delivery address, CUNO/ADID). Not implementation evidence.')
C013 = CONF('22779013', 6, 'Historical upstream ADF/M3 address and HasProjectPrice analysis; not evidence of current in-process behaviour.')
C292 = CONF('22774292', 13, '2024 conversion design: PICo/Table Event streams for CustomerAddress and ProjectWithPrice; design intent, not deployment proof.')

P = 'customerproject'
n = lambda s: f'comp:customerproject.{s}'
# ===== A. Functions root =====
func_nodes = [
 N(n('func.domain-input'), 'Domain update queue', 'external', 'Boundary input carrying UpdateCustomerAddressFromSnapshot and UpdateProjectWithPriceFromSnapshot from Ingress SNAPSHOT.', 'outside', [DOMH, CPREG('q:customerproject-domain triggers customerproject.fn.domain.')], owner=P, flowEntityId='q:customerproject-domain'),
 N(n('func.snapshot-apply'), 'Apply ingress snapshots (DOMAIN)', 'component', 'Applies address and project-price snapshots to domain storage and decides whether project work is needed. Open for the snapshot-apply sub-layer.', 'inside', [DOMH, UCA, UPP], opensLayerId='layer:customerproject.func-app.domain-update'),
 N(n('func.command-input'), 'Command queue', 'external', 'Boundary input for COMMANDS: commands from DOMAIN, the API, operators/seed kit, and handlers that requeue follow-up work. Requeue from a handler to this same queue is shown as a self-requeue badge, not as an arrow back.', 'outside', [CMDH, QCL, CPREG('q:customerproject triggers customerproject.fn.commands; fn.domain and api.http publish to it.')], ['Which handlers requeue which subjects is described per component; Flow v2 renders this as a badge on COMMANDS.'], owner=P, flowEntityId='q:customerproject'),
 N(n('func.project-lifecycle'), 'Project creation and date sync', 'component', 'Creates projects from ERP delivery-address references, keeps project dates aligned with address dates, and backfills missing projects. Publishes CustomerProjectChanged; self-requeues PopulateOrderReferences / CreateProjectsFromErpReferences / HandleCustomerAddressChanged (badge).', 'inside', [CMDH, CFE, CAC, CMP, SPD]),
 N(n('func.address-upsert'), 'Customer address upsert', 'component', 'Validates UpsertCustomerAddress commands, creates or updates the address in ERP (computing candidate address ids), then stores it locally.', 'inside', [CMDH, UCAH, ERPS]),
 N(n('func.order-association'), 'Order association', 'component', 'Associates ERP orders with projects from OrderHistory and on OrderAddressIdChanged; stores OrderReferences and publishes CustomerProjectOrderReferenceChanged. Self-requeues PopulateOrderReferences per project (badge).', 'inside', [CMDH, IOR, OAC]),
 N(n('func.notification'), 'Order notification', 'component', 'Turns order-reference changes and OrderDelivered into ValidateOrder, validates the order against the project, and emits email/SMS SendNotification requests to the communication service. ValidateOrder and SendNotification are self-requeued (badge).', 'inside', [CMDH, ORC, ODH, VOH, PVS, SNH, NSND]),
 N(n('func.image-import'), 'Project/image import', 'component', 'Imports projects, contact lists and images from import commands (migration-style data load).', 'inside', [CMDH, IPS, IPIS, CPIS], ['Migration namespace suggests one-off data load; current operational use is not evidenced.']),
 N(n('func.storage'), 'CustomerProject SQL storage', 'store', 'Projects, customer addresses, ProjectWithPrice, OrderReferences, images metadata and snapshot bookkeeping (EF). Physical hosting not asserted.', 'outside', [STOR, MIG_DEST, MIG_SNAP], owner=P),
 N(n('func.topic'), 'CustomerProject topic', 'external', 'Boundary output for CustomerProjectChanged and CustomerProjectOrderReferenceChanged events to subscribers (including dsp-price and, in cloud, this service).', 'outside', [TOPIC, CFE, IOR, CPREG('fn.commands publishes PCO events to t:customerproject-topic.')], owner=P, flowEntityId='t:customerproject-topic'),
 N(n('func.communication'), 'Communication queue', 'external', 'Boundary output: SendEmail/SendSms messages for the communication service, which is outside this repo.', 'outside', [CQC, NSND, CPREG('fn.commands publishes notifications to q:communication.')], flowEntityId='q:communication'),
 N(n('func.erp'), 'ERP customer-address API', 'external', 'External M3 ERP customer-address create/lookup/update endpoint.', 'outside', [ERPC, FPROG], ['Endpoint identity and auth are configuration-driven and not recorded here.']),
 N(n('func.order-history'), 'OrderHistory service', 'external', 'External HTTP order history used for association and notification validation.', 'outside', [OHC, FPROG]),
 N(n('func.product-catalog'), 'Product catalog service', 'external', 'External HTTP product lookup used during notification validation.', 'outside', [PRC, PVS]),
 N(n('func.image-blob'), 'Image blob storage', 'store', 'Azure Blob container holding processed project images.', 'outside', [BLOB, IMGS], owner=P),
]
f = lambda s: n('func.' + s)
func_edges = [
 E('aedge:customerproject.func.domain-in', f('domain-input'), f('snapshot-apply'), 'message', 'Snapshot update commands', 'Two subject families from Ingress SNAPSHOT.', [DOMH]),
 E('aedge:customerproject.func.snapshot-store', f('snapshot-apply'), f('storage'), 'data', 'Address / ProjectWithPrice rows', 'Snapshot destinations with bookkeeping.', [UCA_KEY, UPP]),
 E('aedge:customerproject.func.snapshot-commands', f('snapshot-apply'), f('command-input'), 'message', 'Create / address-changed commands', 'CreateProjectsFromErpReferences (new delivery address) or HandleCustomerAddressChanged.', [UCA]),
 E('aedge:customerproject.func.cmd-lifecycle', f('command-input'), f('project-lifecycle'), 'message', 'Project commands', 'CreateProjectsFromErpReferences, HandleCustomerAddressChanged, CreateMissingProjects, SyncProjectDates.', [CMDH]),
 E('aedge:customerproject.func.cmd-upsert', f('command-input'), f('address-upsert'), 'message', 'UpsertCustomerAddress', 'Address upsert command.', [CMDH]),
 E('aedge:customerproject.func.cmd-orders', f('command-input'), f('order-association'), 'message', 'Order-reference commands', 'PopulateOrderReferences, ImportOrderReferencesForProjects, OrderAddressIdChanged.', [CMDH]),
 E('aedge:customerproject.func.cmd-notify', f('command-input'), f('notification'), 'message', 'Order/notification messages', 'CustomerProjectOrderReferenceChanged, OrderDelivered, ValidateOrder, SendNotification.', [CMDH]),
 E('aedge:customerproject.func.cmd-import', f('command-input'), f('image-import'), 'message', 'Import commands', 'ImportCustomerProjects, ImportProjectContactsList, ImportImage.', [CMDH]),
 E('aedge:customerproject.func.lifecycle-store', f('project-lifecycle'), f('storage'), 'data', 'Projects', 'Reads addresses, upserts projects.', [CFE, CAC]),
 E('aedge:customerproject.func.lifecycle-topic', f('project-lifecycle'), f('topic'), 'message', 'CustomerProjectChanged', 'Published after create/date change.', [CFE, CAC]),
 E('aedge:customerproject.func.upsert-erp', f('address-upsert'), f('erp'), 'dependency', 'Create/update address', 'ERP is authoritative for address ids.', [ERPS, ERPC]),
 E('aedge:customerproject.func.upsert-store', f('address-upsert'), f('storage'), 'data', 'Customer addresses', 'Local upsert after ERP call.', [UCAH]),
 E('aedge:customerproject.func.orders-history', f('order-association'), f('order-history'), 'dependency', 'Orders by customer/address', 'Source of candidate orders.', [IOR, OHC]),
 E('aedge:customerproject.func.orders-store', f('order-association'), f('storage'), 'data', 'OrderReferences', 'Upsert order references.', [IOR, OAC]),
 E('aedge:customerproject.func.orders-topic', f('order-association'), f('topic'), 'message', 'CustomerProjectOrderReferenceChanged', 'Published when an order reference changes.', [IOR, OAC]),
 E('aedge:customerproject.func.notify-history', f('notification'), f('order-history'), 'dependency', 'Order by id', 'Validation input.', [PVS]),
 E('aedge:customerproject.func.notify-product', f('notification'), f('product-catalog'), 'dependency', 'Products', 'Validation input.', [PVS]),
 E('aedge:customerproject.func.notify-store', f('notification'), f('storage'), 'data', 'Project by order', 'Read only.', [VOH]),
 E('aedge:customerproject.func.notify-comm', f('notification'), f('communication'), 'message', 'SendEmail / SendSms', 'Email immediately; SMS scheduled per business hours.', [NSND, CQC, VOH]),
 E('aedge:customerproject.func.import-store', f('image-import'), f('storage'), 'data', 'Imported projects / image rows', 'Import writes.', [IPS, CPIS]),
 E('aedge:customerproject.func.import-blob', f('image-import'), f('image-blob'), 'data', 'Image files', 'Processed upload.', [CPIS, IMGS]),
 E('aedge:customerproject.func.topic-forward', f('topic'), f('command-input'), 'message', 'CustomerProjectOrderReferenceChanged (cloud)', 'Cloud subscription customerproject-to-customerproject forwards this event back to the command queue.', [CPREG('s:customerproject-topic/customerproject-to-customerproject forwards to q:customerproject, cloud scope only.')], ['Registered as cloud-scope only; locally the topic forwards to an inspect queue instead.']),
]
func_layer = L('layer:customerproject.func-app', P, 'Functions \u2014 domain and command responsibilities',
 'Inside the Functions compute container: DOMAIN applies Ingress snapshots, COMMANDS runs project, address, order-association, notification and import work. Queues, the topic, SQL, blob and external HTTP services are boundary dependencies. Arrows explain responsibilities, not execution order or observed events; handler self-requeues are badges.',
 None, 'container:customerproject.func-app',
 [CPREG('func-app container with DOMAIN and COMMANDS handlers.'), DOMH, CMDH, FPROG, APPDI, C513, C013, C292],
 ['Local source HEAD b0864c051065f88e978b6d110483096c915d7edf at review (clean); deployment freshness unknown. Per-file hashes identify reviewed evidence.',
  'Confluence 22774292 (v13) describes the event-stream conversion that produces these snapshots; it does not prove rollout. Confluence 22779013 (v6) HasProjectPrice logic is historical ADF analysis and differs from current snapshot handling; no reconciliation is asserted.',
  'Confluence 22779513 (v4) frames project vs delivery address; current code creates projects only for NEW Delivery-type addresses (UpdateCustomerAddressFromSnapshotHandler lines 47\u201360). Whether that matches business intent is unverified.',
  'Dahl.Core MessageHandler retry/claim-check semantics (HandleWithRetry, HandleOnce, short in-process retry) are outside reviewed source; no retry topology is drawn.',
  'CustomerProjectService (ERP-backed create/update) is registered only in the API host, not in Functions (Application ServiceCollectionExtensions lines 17\u201349).'],
 func_nodes, func_edges)

# ===== B. nested child =====
d = lambda s: n('domain-update.' + s)
child_nodes = [
 N(d('input'), 'Domain update queue', 'external', 'Boundary input; same Flow entity as the parent layer\u2019s domain input.', 'outside', [DOMH], owner=P, flowEntityId='q:customerproject-domain'),
 N(d('address-apply'), 'Customer-address snapshot apply', 'component', 'Persists CustomerAddressSnapshot through the shared storable-entity snapshot base (composite key: DataSource, CustomerId, AddressType, ErpAddressId), with bookkeeping that discards unchanged/out-of-date snapshots.', 'inside', [UCA_KEY, UCA_FLAG]),
 N(d('project-trigger'), 'Project follow-up decision', 'component', 'After a saved address: a newly created Delivery address requests project creation from its ERP reference; a modified address requests address-changed handling; non-delivery new addresses stop here.', 'inside', [UCA]),
 N(d('price-apply'), 'Project-price snapshot apply', 'component', 'Persists ProjectWithPriceSnapshot rows only; deliberately publishes no event.', 'inside', [UPP]),
 N(d('snapshot-store'), 'Snapshot destinations + bookkeeping', 'store', 'CustomerAddress and ProjectWithPrice tables as snapshot destinations plus snapshot bookkeeping.', 'outside', [MIG_DEST, MIG_SNAP, STOR], owner=P),
 N(d('feature-flags'), 'DSP feature management', 'external', 'Feature flag EnableM3CloudEventSendingOnNoChange can let discarded (no-change) address saves still emit follow-up commands.', 'outside', [UCA_FLAG], ['Flag value per environment is not recorded.']),
 N(d('commands-out'), 'Command queue', 'external', 'Boundary output to COMMANDS (parent layer\u2019s project lifecycle).', 'outside', [UCA, CPREG('fn.domain publishes to q:customerproject.')], owner=P, flowEntityId='q:customerproject'),
]
child_edges = [
 E('aedge:customerproject.domain-update.address-in', d('input'), d('address-apply'), 'message', 'UpdateCustomerAddressFromSnapshot', 'Address snapshot batch.', [DOMH]),
 E('aedge:customerproject.domain-update.price-in', d('input'), d('price-apply'), 'message', 'UpdateProjectWithPriceFromSnapshot', 'Project-price snapshot batch.', [DOMH]),
 E('aedge:customerproject.domain-update.address-store', d('address-apply'), d('snapshot-store'), 'data', 'CustomerAddress', 'Upsert with bookkeeping.', [UCA_KEY]),
 E('aedge:customerproject.domain-update.price-store', d('price-apply'), d('snapshot-store'), 'data', 'ProjectWithPrice', 'Upsert with bookkeeping.', [UPP]),
 E('aedge:customerproject.domain-update.flag', d('address-apply'), d('feature-flags'), 'dependency', 'No-change override', 'Consulted only when a save is discarded as unchanged.', [UCA_FLAG]),
 E('aedge:customerproject.domain-update.decide', d('address-apply'), d('project-trigger'), 'data', 'Saved address', 'Created-vs-modified and AddressType drive the decision.', [UCA]),
 E('aedge:customerproject.domain-update.commands', d('project-trigger'), d('commands-out'), 'message', 'CreateProjectsFromErpReferences / HandleCustomerAddressChanged', 'Follow-up work for COMMANDS.', [UCA]),
]
child_layer = L('layer:customerproject.func-app.domain-update', P, 'DOMAIN \u2014 applying ingress snapshots',
 'Second-level drill-down of Functions \u2192 \u201cApply ingress snapshots\u201d. Shows how address and project-price snapshots become domain rows and when address changes trigger project work. Conceptual responsibilities only.',
 'layer:customerproject.func-app', None, [DOMH, UCA, UPP, C292],
 ['Dahl.Core ImportStorableEntityFromSnapshotBase internals (ordering/staleness rules) are not in this repo; bookkeeping is described by name only.',
  'Confluence 22774292 v13 describes the same CustomerAddress/ProjectWithPrice stream conversion as design intent; current code is authoritative here and rollout is not inferred.'],
 child_nodes, child_edges)

# ===== C. API root =====
a = lambda s: n('api.' + s)
api_nodes = [
 N(a('callers'), 'HTTP callers (Okta)', 'external', 'Authenticated web/API clients; HTTP is the trigger surface of this container.', 'outside', [APROG, CPREG('customerproject.api.http handler.')], ['Concrete client applications are not evidenced in this repo.']),
 N(a('project-controller'), 'CustomerProject controller', 'component', 'Tenant-scoped project read/search, create/update, and operator endpoints that only enqueue commands (import, import-contacts, import-order-references, create-missing-projects, synchronize-project-dates).', 'inside', [CPC]),
 N(a('order-association-controller'), 'OrderAssociation controller', 'component', 'Manual order\u2194project association/removal and populate-orders trigger.', 'inside', [OAC_CTRL]),
 N(a('image-controller'), 'Image controller', 'component', 'Project image read/list/upload/delete and send-import-command.', 'inside', [IMG_CTRL]),
 N(a('environmental-profile-controller'), 'EnvironmentalProfile controller', 'component', 'Maintains the environmental profile list directly in storage.', 'inside', [ENV_CTRL]),
 N(a('project-service'), 'Project create/update service', 'component', 'Validates projects, creates/updates the delivery address in ERP, upserts the project, publishes CustomerProjectChanged and requests PopulateOrderReferences.', 'inside', [CPS, APPDI]),
 N(a('manual-association'), 'Manual association service', 'component', 'Applies manual order associations, upserts OrderReferences and publishes CustomerProjectOrderReferenceChanged.', 'inside', [MAS]),
 N(a('image-service'), 'Image service', 'component', 'Processes and stores images in blob, image metadata in SQL.', 'inside', [CPIS, IMGS]),
 N(a('storage'), 'CustomerProject SQL storage', 'store', 'Same SQL storage as Functions (shared database, separate host).', 'outside', [STOR, APROG], owner=P),
 N(a('commands-out'), 'Command queue', 'external', 'Boundary output to Functions COMMANDS.', 'outside', [QCL, CPC, CPREG('api.http publishes commands to q:customerproject.')], owner=P, flowEntityId='q:customerproject'),
 N(a('topic'), 'CustomerProject topic', 'external', 'Boundary output for project and order-reference events.', 'outside', [TOPIC, CPS, MAS, CPREG('api.http publishes to t:customerproject-topic.')], owner=P, flowEntityId='t:customerproject-topic'),
 N(a('erp'), 'ERP customer-address API', 'external', 'External M3 ERP address create/update.', 'outside', [ERPC, APROG]),
 N(a('image-blob'), 'Image blob storage', 'store', 'Processed project images.', 'outside', [BLOB], owner=P),
]
api_edges = [
 E('aedge:customerproject.api.http-projects', a('callers'), a('project-controller'), 'message', 'Project HTTP', 'Requests; not observed traffic.', [CPC]),
 E('aedge:customerproject.api.http-orders', a('callers'), a('order-association-controller'), 'message', 'Order association HTTP', 'Requests.', [OAC_CTRL]),
 E('aedge:customerproject.api.http-images', a('callers'), a('image-controller'), 'message', 'Image HTTP', 'Requests incl. multipart upload.', [IMG_CTRL]),
 E('aedge:customerproject.api.http-env', a('callers'), a('environmental-profile-controller'), 'message', 'Environmental profile HTTP', 'Requests.', [ENV_CTRL]),
 E('aedge:customerproject.api.projects-read', a('project-controller'), a('storage'), 'data', 'Project reads', 'Read/search via storage reader.', [CPC]),
 E('aedge:customerproject.api.projects-write', a('project-controller'), a('project-service'), 'dependency', 'Create / update', 'POST/PUT delegate to ICustomerProjectService.', [CPC]),
 E('aedge:customerproject.api.projects-cmd', a('project-controller'), a('commands-out'), 'message', 'Operator commands', 'ImportCustomerProjects, ImportProjectContactsList, ImportOrderReferencesForProjects, CreateMissingProjects, SyncProjectDates.', [CPC]),
 E('aedge:customerproject.api.service-erp', a('project-service'), a('erp'), 'dependency', 'Address create/update', 'ERP first, then local upsert.', [CPS, ERPS]),
 E('aedge:customerproject.api.service-store', a('project-service'), a('storage'), 'data', 'Projects', 'Upsert.', [CPS]),
 E('aedge:customerproject.api.service-topic', a('project-service'), a('topic'), 'message', 'CustomerProjectChanged', 'After create/update.', [CPS]),
 E('aedge:customerproject.api.service-cmd', a('project-service'), a('commands-out'), 'message', 'PopulateOrderReferences', 'Follow-up for Functions.', [CPS]),
 E('aedge:customerproject.api.orders-assoc', a('order-association-controller'), a('manual-association'), 'dependency', 'Associate / remove', 'Manual association.', [OAC_CTRL]),
 E('aedge:customerproject.api.orders-cmd', a('order-association-controller'), a('commands-out'), 'message', 'PopulateOrderReferences', 'orders/populate endpoint.', [OAC_CTRL]),
 E('aedge:customerproject.api.assoc-store', a('manual-association'), a('storage'), 'data', 'OrderReferences', 'Upsert.', [MAS]),
 E('aedge:customerproject.api.assoc-topic', a('manual-association'), a('topic'), 'message', 'CustomerProjectOrderReferenceChanged', 'Published on change.', [MAS]),
 E('aedge:customerproject.api.images-svc', a('image-controller'), a('image-service'), 'dependency', 'Image operations', 'Read/upload/remove.', [IMG_CTRL]),
 E('aedge:customerproject.api.images-cmd', a('image-controller'), a('commands-out'), 'message', 'ImportImage', 'send-import-command endpoint.', [IMG_CTRL]),
 E('aedge:customerproject.api.images-blob', a('image-service'), a('image-blob'), 'data', 'Image files', 'Blob upload/delete.', [CPIS, IMGS]),
 E('aedge:customerproject.api.images-meta', a('image-service'), a('storage'), 'data', 'Image metadata', 'Project image rows.', [CPIS]),
 E('aedge:customerproject.api.env-store', a('environmental-profile-controller'), a('storage'), 'data', 'Environmental profiles', 'Direct read/write.', [ENV_CTRL]),
]
api_layer = L('layer:customerproject.api', P, 'Api \u2014 HTTP entry points and their responsibilities',
 'Inside the Api compute container: four controllers are the HTTP trigger surface; create/update, manual association and image services do synchronous work, while bulk/operator work is handed to Functions via the command queue. Arrows are responsibilities, not observed requests.',
 None, 'container:customerproject.api', [CPREG('api container with HTTP controllers handler.'), APROG, APPDI, C513],
 ['Api Program registers an ingress queue client (Program.cs line 69) but no controller or API feature use was found in reviewed files; not drawn.',
  'Okta authentication and tenant filters are named from Program.cs; policy details are not modelled.',
  'Confluence 22779513 (v4) workshop scope may describe UI flows not implemented here; no UI-side components are drawn.'],
 api_nodes, api_edges)

# ===================== PRICE =====================
PR = 'dsp-price/src/'
p_ = lambda p, note: R(PR + p, note)
PREG = lambda note: REG('price', note)
PQH = p_('Dahl.Price.Functions/Functions/PriceQueueHandler.cs', 'Lines 71\u2013181: COMMANDS subjects: Nexstep/VisionPoint imports and deletes, CheckItemCampaigns, PublishAllItemCampaigns, SaveLowestPriceSnapshot, SaveCampaignLowestPrices.')
PDQ = p_('Dahl.Price.Functions/Functions/PriceDomainQueueHandler.cs', 'Lines 37\u201366: DOMAIN subjects: Update{Campaign,CampaignPrice,DirectDeliverySupplier,ValidProduct}FromSnapshot.')
PDI = p_('Dahl.Price.Functions/Functions/PriceDomainIngressQueueHandler.cs', 'Lines 63\u2013228: DOMAIN (PICO direct) subjects: ~23 M3 *Changed contracts.')
PICO_IM = p_('Dahl.Price.Application/Features/Ingress/Pico/ItemMasterChangedHandler.cs', 'Lines 16\u201320: PICO handlers derive from ImportStorableEntityFromChangeEventBase with storable-entity storage.')
PSUBS = p_('Dahl.Price.Functions/Functions/PriceSubscriptionsHandler.cs', 'Lines 20\u201330: SUBSCRIPTIONS queue trigger (e.g. CustomerChanged).')
PCPS = p_('Dahl.Price.Functions/Functions/CustomerProjectSubscriptionTrigger.cs', 'Lines 25\u201355: CustomerProjectChanged from customerproject topic -> upsert price-side CustomerProject reference.')
PCUS = p_('Dahl.Price.Functions/Functions/CustomerSubscriptionTrigger.cs', 'Customer topic subscription trigger.')
PWHS = p_('Dahl.Price.Functions/Functions/WarehouseSubscriptionTrigger.cs', 'Warehouse topic subscription trigger.')
PCUR = p_('Dahl.Price.Functions/Functions/CurrencySubscriptionTrigger.cs', 'Currency topic subscription trigger.')
PPRS = p_('Dahl.Price.Functions/Functions/ProductSubscriptionTrigger.cs', 'Product topic subscription trigger.')
PBLOB = p_('Dahl.Price.Functions/Functions/OptimeraSnapshotFileBlobTrigger.cs', 'Lines 10\u201340: blob trigger on pricedumpfromoptimera; CSV type by filename -> IPriceImporter.')
PIMPC = p_('Dahl.Price.Import/Configuration/ImportConfiguration.cs', 'Line 8: OptimeraFilesContainerName = pricedumpfromoptimera.')
PT1 = p_('Dahl.Price.Functions/Functions/PriceSnapshotTimerTrigger.cs', 'Line 15: timer 04/12/17 -> SaveLowestPriceSnapshot.')
PT2 = p_('Dahl.Price.Functions/Functions/CheckItemCampaignsTimerTrigger.cs', 'Line 20: hourly timer -> CheckItemCampaigns.')
PT3 = p_('Dahl.Price.Functions/Functions/PriceCampaignLowestPricesTimerTrigger.cs', 'Line 20: timer -> SaveCampaignLowestPrices.')
PCIC = p_('Dahl.Price.Application/Features/Events/CheckItemCampaignsHandler.cs', 'Lines 15\u201352: compute item campaign changes and SendBatchAsync ItemCampaignsChanged to price topic.')
PPAC = p_('Dahl.Price.Application/Features/Admin/PublishAllItemCampaignsHandler.cs', 'Lines 19\u201348: publish all item campaigns to price topic.')
PTOP = p_('Dahl.Price.Messaging/PriceTopicClient.cs', 'Line 15: price topic sender.')
PUPD = p_('Dahl.Price.Application/Features/UpdateFromIngressSnapshot/UpdateOptimeraValidProductFromSnapshotHandler.cs', 'One of four snapshot-apply handlers (ValidProduct).')
PWDB = p_('Dahl.Price.Storage.Write/PriceStorageDbContext.cs', 'Write-side price SQL EF DbContext; no values copied.')
PRDB = p_('Dahl.Price.Storage.Read/PriceDbReader.cs', 'Read-side price SQL access used by price reading.')
# ingress
PIQ = p_('Dahl.Price.Ingress.Functions/Functions/PriceIngressQueueHandler.cs', 'Lines 45\u2013161: INGRESS subjects: 14 M3 *Changed contracts.')
PSQ = p_('Dahl.Price.Ingress.Functions/Functions/PriceSnapshotQueueHandler.cs', 'Lines 30\u2013119: SNAPSHOT subjects: 4 Create*Snapshot and 6 Reload* commands.')
PRT = p_('Dahl.Price.Ingress.Functions/Functions/ReloadM3CloudDataTimerTriggers.cs', 'Lines 14\u201389: six ReloadM3CloudOptimera* timers.')
PCEB = p_('Dahl.Price.Ingress.Application/Features/Import/ChangeEventHandlerBase.cs', 'Line 55: import handlers send snapshot commands to the snapshot queue.')
PCSN = p_('Dahl.Price.Ingress.Application/Features/CreateIngressSnapshot/CreateOptimeraValidProductSnapshotOnItemMasterChangedHandler.cs', 'Lines 8\u201318: build snapshots from ingress storage and SendBatchAsync to domain queue.')
PRLB = p_('Dahl.Price.Ingress.Application/Features/FullReload/ReloadHandlerBase.cs', 'Lines 11\u201333: write reloaded models as CSV to staging blob then move to destination container.')
PBSC = p_('Dahl.Price.Ingress.Application/Features/FullReload/BlobStorageClient.cs', 'Lines 13\u201340: staging pricedumpfromoptimerastaging -> destination pricedumpfromoptimera.')
PIST = p_('Dahl.Price.Ingress.Storage/Configuration/ConfigurationExtensions.cs', 'Ingress SQL storage registration; no values copied.')
# api
PADM = p_('Dahl.Price.Api/V3/Controllers/AdminController.cs', 'Lines 14\u201344: DspAdmin delete-nexstep-pricelist enqueues DeleteNexstepPriceList; also injects IIngressQueueClient.')
PV3 = p_('Dahl.Price.Api/V3/Controllers/PricesController.cs', 'Lines 17\u201341: V3 prices endpoints via IPriceReader, customer reader, unit/currency helpers, cost price reader.')
PV3D = p_('Dahl.Price.Api/V3/Controllers/PricesDspController.cs', 'Lines 18\u201373: prices/dsp endpoints.')
PV3M = p_('Dahl.Price.Api/V3/Controllers/PricesM3ApiController.cs', 'Lines 18\u201373: prices/m3api endpoints.')
PV2 = p_('Dahl.Price.Api/V2/Controllers/PricesController.cs', 'Lines 11\u201330: legacy V2 prices via IPriceReader.')
PV1 = p_('Dahl.Price.Api/V1/Controllers/PricesController.cs', 'Lines 12\u201397: legacy V1 prices via IPriceReader.')
PCAM = p_('Dahl.Price.Api/V3/Controllers/CampaignsController.cs', 'Lines 11\u201333: campaigns via IPriceReader.')
PSAL = p_('Dahl.Price.Api/V3/Controllers/SalesDepartmentController.cs', 'Lines 15\u201328: price report via IPriceReportGenerator.')
PRDR = p_('Dahl.Price.Application/Features/GetPrices/PriceReader.cs', 'Lines 15\u201379: strategy selection by tenant, DataSource and DSP-calculation feature flags/override.')
PODS = p_('Dahl.Price.Application/Features/GetPrices/PriceReaderStrategies/OptimeraDsp.cs', 'Lines 16\u2013105: DSP strategy uses DB readers + DspCalculationPriceRetriever.')
POM3 = p_('Dahl.Price.Application/Features/GetPrices/PriceReaderStrategies/OptimeraM3Api.cs', 'Lines 16\u2013105: M3 API strategy uses DB readers + M3PriceRetriever.')
PDOP = p_('Dahl.Price.Application/Features/GetPrices/PriceReaderStrategies/DahlOnPrem.cs', 'Lines 15\u201347: Dahl on-prem strategy falls back to M3 prices for missing data.')
PDCR = p_('Dahl.Price.Application/Features/GetPrices/DataRetrievers/DspCalculationPriceRetriever.cs', 'Lines 10\u2013165: DSP calculation via IPriceCalculator, with fallback to M3PriceRetriever.')
PM3R = p_('Dahl.Price.Application/Features/GetPrices/DataRetrievers/M3PriceRetriever.cs', 'Lines 11\u201314: M3 prices via IErpPriceClient.')
PDDR = p_('Dahl.Price.Application/Features/GetPrices/DataRetrievers/DirectDeliveryItemsResolver.cs', 'Line 7: direct-delivery items from price DB.')
PCALC = p_('Dahl.Price.Calculation/PriceCalculator.cs', 'Lines 16\u201328: calculator composed of customer, item master, order-type, price-list matrix/table, discount, direct-delivery and agreement resolvers.')
PERP = p_('Dahl.Price.Integrations.Erp/ErpPriceClient.cs', 'Line 9: ERP price client wrapping IPriceClient.')
PDOC = R('dsp-price/docs/PriceCalculator.md', 'Repo design doc for the price calculator; may lag code.', 'uncertain')
PBAT = p_('Dahl.Price.Api.Batch/V3/Controllers/PricesBatchController.cs', 'Lines 16\u201330: batch prices via IPriceReader.')
PBATD = p_('Dahl.Price.Api.Batch/V3/Controllers/PricesBatchDspController.cs', 'Lines 17\u201331: batch DSP prices via IPriceReader.')
PBATM = p_('Dahl.Price.Api.Batch/V3/Controllers/PricesBatchM3ApiController.cs', 'Lines 17\u201331: batch M3 API prices via IPriceReader.')
PBRD = p_('Dahl.Price.Application/Features/GetPrices/PriceBatchReaderDsp.cs', 'Batch DSP reader (uses price calculator).')
PBRM = p_('Dahl.Price.Application/Features/GetPrices/PriceReaderStrategies/PriceBatchReaderM3Api.cs', 'Batch M3 API reader.')

TREE_NOTE = 'dsp-price copy has no .git; evidence identified by per-file sha256 (Team Lead ruled HEAD check unnecessary).'
q = lambda s: f'comp:price.{s}'
# ---- price func-app ----
pf = lambda s: q('func.' + s)
pfn = [
 N(pf('commands-input'), 'Price command queue', 'external', 'Boundary input for COMMANDS (imports, campaign checks, lowest-price snapshots). Timer handlers and API enqueue here; command handlers that requeue are badges.', 'outside', [PQH, PREG('q:price triggers price.fn.commands.')], owner='price', flowEntityId='q:price'),
 N(pf('timers'), 'Scheduled triggers', 'component', 'Three timers enqueue SaveLowestPriceSnapshot, CheckItemCampaigns and SaveCampaignLowestPrices.', 'inside', [PT1, PT2, PT3]),
 N(pf('commands'), 'Price list & campaign commands', 'component', 'Nexstep/VisionPoint price list and campaign import/delete, campaign change detection and lowest-price snapshots.', 'inside', [PQH, PCIC, PPAC]),
 N(pf('domain-input'), 'Price domain queue', 'external', 'Boundary input of Optimera snapshot updates from Ingress SNAPSHOT.', 'outside', [PDQ, PREG('q:price-domain triggers price.fn.domain.')], owner='price', flowEntityId='q:price-domain'),
 N(pf('snapshot-apply'), 'Apply Optimera snapshots', 'component', 'Applies Campaign, CampaignPrice, DirectDeliverySupplier and ValidProduct snapshots.', 'inside', [PDQ, PUPD]),
 N(pf('pico-input'), 'PICO direct queue', 'external', 'Boundary input of ~23 M3 *Changed contracts that bypass ingress/snapshot.', 'outside', [PDI, PREG('q:price-domain-ingress; registration notes PICO inject bypasses ingress/snapshot.')], ['Upstream sender is outside both repos (registration inject ext:m3-pico).'], owner='price', flowEntityId='q:price-domain-ingress'),
 N(pf('pico-import'), 'M3 calculation-data import', 'component', 'Persists M3 reference data used by DSP price calculation (item master, matrices, agreements, discounts, currency, VAT, etc.) via storable-entity storage.', 'inside', [PDI, PICO_IM]),
 N(pf('subs-input'), 'Reference-data subscriptions', 'external', 'Boundary inputs: customerproject, customer, warehouse, currency and product topic subscriptions plus the SUBSCRIPTIONS queue.', 'outside', [PCPS, PCUS, PWHS, PCUR, PPRS, PSUBS, PREG('Five price-to-* subscriptions and q:price-subscriptions.')], ['Grouped; only the customerproject subscription is linked to a Flow entity here to avoid one node per subscription.'], flowEntityId='s:customerproject-topic/price-to-customerproject-subscription'),
 N(pf('reference-sync'), 'Reference-data sync', 'component', 'Upserts price-side copies of customer project, customer, warehouse, currency and product reference data.', 'inside', [PCPS, PSUBS]),
 N(pf('optimera-files'), 'Optimera file container', 'store', 'Blob container pricedumpfromoptimera; written by Ingress full reload, read by the blob trigger.', 'outside', [PBLOB, PIMPC, PREG('b:pricedumpfromoptimera triggers price.fn.blob-optimera.')], owner='price', flowEntityId='b:pricedumpfromoptimera'),
 N(pf('file-import'), 'Optimera CSV import', 'component', 'Imports ValidProducts/Campaigns/CampaignPrices/... CSV snapshots via IPriceImporter.', 'inside', [PBLOB]),
 N(pf('price-db'), 'Price SQL storage', 'store', 'Write-side price database (prices, campaigns, calculation reference data).', 'outside', [PWDB], owner='price'),
 N(pf('topic'), 'Price topic', 'external', 'Boundary output for ItemCampaignsChanged.', 'outside', [PTOP, PCIC, PREG('fn.commands publishes ItemCampaignsChanged to t:price.')], owner='price', flowEntityId='t:price'),
]
pfe = [
 E('aedge:price.func.timers-cmd', pf('timers'), pf('commands-input'), 'message', 'Scheduled commands', 'Three timer-produced subjects.', [PT1, PT2, PT3]),
 E('aedge:price.func.cmd-in', pf('commands-input'), pf('commands'), 'message', 'Price commands', 'Subject families per PriceQueueHandler.', [PQH]),
 E('aedge:price.func.cmd-db', pf('commands'), pf('price-db'), 'data', 'Price lists / campaigns / snapshots', 'Import and snapshot writes.', [PQH]),
 E('aedge:price.func.cmd-topic', pf('commands'), pf('topic'), 'message', 'ItemCampaignsChanged', 'From campaign checks / publish-all.', [PCIC, PPAC]),
 E('aedge:price.func.domain-in', pf('domain-input'), pf('snapshot-apply'), 'message', 'Update*FromSnapshot', 'Four Optimera snapshot kinds.', [PDQ]),
 E('aedge:price.func.domain-db', pf('snapshot-apply'), pf('price-db'), 'data', 'Snapshot rows', 'Snapshot destinations.', [PUPD]),
 E('aedge:price.func.pico-in', pf('pico-input'), pf('pico-import'), 'message', 'M3 *Changed', 'PICO change events.', [PDI]),
 E('aedge:price.func.pico-db', pf('pico-import'), pf('price-db'), 'data', 'Calculation reference data', 'Storable entities.', [PICO_IM]),
 E('aedge:price.func.subs-in', pf('subs-input'), pf('reference-sync'), 'message', 'Reference events', 'e.g. CustomerProjectChanged.', [PCPS, PSUBS]),
 E('aedge:price.func.subs-db', pf('reference-sync'), pf('price-db'), 'data', 'Reference copies', 'Upserts.', [PCPS]),
 E('aedge:price.func.blob-in', pf('optimera-files'), pf('file-import'), 'data', 'CSV files', 'Blob trigger per file.', [PBLOB]),
 E('aedge:price.func.blob-db', pf('file-import'), pf('price-db'), 'data', 'Imported Optimera data', 'Import writes.', [PBLOB]),
]
p_func = L('layer:price.func-app', 'price', 'Functions \u2014 price import, sync and campaign responsibilities',
 'Inside the price Functions container: commands, Optimera snapshot apply, PICO direct import, reference-data subscriptions and Optimera CSV import all maintain the price database; campaign changes are published on the price topic. Conceptual only.',
 None, 'container:price.func-app', [PREG('price func-app container with 12 handlers.'), PQH, PDQ, PDI, PBLOB],
 [TREE_NOTE, 'Per-handler internals of ~23 PICO and 14 command subjects are grouped, not enumerated.', 'Write-side storage type/topology beyond EF registration is not asserted.', 'No price Confluence pages were opened for this layer; repo docs only.'],
 pfn, pfe)

# ---- price ingress-app ----
pi = lambda s: q('ingress.' + s)
pin = [
 N(pi('input'), 'Price ingress queue', 'external', 'Boundary input of 14 M3 *Changed contracts (locally forwarded from per-entity topic subscriptions).', 'outside', [PIQ, PREG('q:price-ingress triggers price.fn.ingress; local subscriptions forward into it.')], owner='price', flowEntityId='q:price-ingress'),
 N(pi('import'), 'Change-event import', 'component', 'Persists ingress models and requests snapshot creation.', 'inside', [PIQ, PCEB]),
 N(pi('storage'), 'Price ingress SQL', 'store', 'Persisted ingress models for snapshot composition.', 'outside', [PIST], owner='price'),
 N(pi('snapshot-queue'), 'Price snapshot queue', 'external', 'Decouples import from snapshot composition and full reload.', 'outside', [PSQ, PCEB, PREG('q:price-snapshot triggers price.fn.snapshot.')], owner='price', flowEntityId='q:price-snapshot'),
 N(pi('reload-timers'), 'M3Cloud reload timers', 'component', 'Six timers enqueue Reload* commands.', 'inside', [PRT]),
 N(pi('snapshots'), 'Optimera snapshot composition', 'component', 'Builds DirectDeliverySupplier and ValidProduct snapshots from ingress storage and sends them to the domain queue.', 'inside', [PSQ, PCSN]),
 N(pi('full-reload'), 'Optimera full reload', 'component', 'Writes full Optimera datasets as CSV to staging blob and moves them to the Optimera file container.', 'inside', [PSQ, PRLB, PBSC]),
 N(pi('domain-out'), 'Price domain queue', 'external', 'Boundary output to Functions DOMAIN.', 'outside', [PCSN, PREG('price.fn.snapshot publishes to q:price-domain.')], owner='price', flowEntityId='q:price-domain'),
 N(pi('optimera-files'), 'Optimera file container', 'store', 'Destination container pricedumpfromoptimera consumed by Functions blob trigger.', 'outside', [PBSC, PIMPC], ['Registration has no publish edge from ingress-app to b:pricedumpfromoptimera; this code-evidenced link is not reflected in Flow.'], owner='price', flowEntityId='b:pricedumpfromoptimera'),
]
pie = [
 E('aedge:price.ingress.in', pi('input'), pi('import'), 'message', 'M3 *Changed', '14 subjects.', [PIQ]),
 E('aedge:price.ingress.persist', pi('import'), pi('storage'), 'data', 'Ingress models', 'Persist.', [PCEB]),
 E('aedge:price.ingress.request', pi('import'), pi('snapshot-queue'), 'message', 'Create*Snapshot', 'Snapshot requests.', [PCEB]),
 E('aedge:price.ingress.reload-req', pi('reload-timers'), pi('snapshot-queue'), 'message', 'Reload* (6)', 'Scheduled full reloads.', [PRT]),
 E('aedge:price.ingress.compose', pi('snapshot-queue'), pi('snapshots'), 'message', 'Create*Snapshot', 'Four subjects.', [PSQ]),
 E('aedge:price.ingress.reload', pi('snapshot-queue'), pi('full-reload'), 'message', 'Reload*', 'Six subjects.', [PSQ]),
 E('aedge:price.ingress.read', pi('snapshots'), pi('storage'), 'data', 'Snapshot queries', 'Read side.', [PCSN]),
 E('aedge:price.ingress.out', pi('snapshots'), pi('domain-out'), 'message', 'Update*FromSnapshot', 'Batch send.', [PCSN]),
 E('aedge:price.ingress.files', pi('full-reload'), pi('optimera-files'), 'data', 'Optimera CSVs', 'Staging then move.', [PRLB, PBSC]),
]
p_ing = L('layer:price.ingress-app', 'price', 'Ingress \u2014 M3 import, snapshots and full reload',
 'Inside the price Ingress container: import M3 change events, compose Optimera snapshots for the domain, and periodically write full Optimera datasets as CSV files for Functions to import.',
 None, 'container:price.ingress-app', [PREG('price ingress-app container (port unknown).'), PIQ, PSQ, PRT],
 [TREE_NOTE, 'Ingress port is unknown in registration (label "port ?").', 'Where full-reload data is read from (M3Cloud source) is not traced in reviewed files.'],
 pin, pie)

# ---- price api + child ----
pa = lambda s: q('api.' + s)
pan = [
 N(pa('callers'), 'HTTP callers', 'external', 'Authenticated price API clients.', 'outside', [PV3, PREG('price.api.http handler.')], ['Concrete clients not evidenced.']),
 N(pa('price-controllers'), 'Price controllers (V1\u2013V3)', 'component', 'get-prices endpoints: legacy V1/V2, V3, V3 /dsp and /m3api variants; all delegate to the price reader.', 'inside', [PV1, PV2, PV3, PV3D, PV3M]),
 N(pa('campaigns'), 'Campaigns controllers', 'component', 'Campaign queries via the price reader.', 'inside', [PCAM]),
 N(pa('sales-report'), 'Sales department report', 'component', 'Generates price reports.', 'inside', [PSAL], ['Report generator internals not reviewed.']),
 N(pa('admin'), 'Admin controller', 'component', 'DspAdmin-only delete of a Nexstep price list, handed to Functions COMMANDS.', 'inside', [PADM]),
 N(pa('price-reader'), 'Price reading', 'component', 'Selects a tenant/data-source strategy and returns prices from DSP calculation or M3. Open for the price-reading sub-layer.', 'inside', [PRDR], opensLayerId='layer:price.api.get-prices'),
 N(pa('price-db'), 'Price SQL (read)', 'store', 'Read-side price database.', 'outside', [PRDB], owner='price'),
 N(pa('erp'), 'M3 price API', 'external', 'External ERP price endpoint.', 'outside', [PERP]),
 N(pa('commands-out'), 'Price command queue', 'external', 'Boundary output to Functions COMMANDS.', 'outside', [PADM, PREG('price.api.http publishes DeleteNexstepPriceList to q:price.')], owner='price', flowEntityId='q:price'),
]
pae = [
 E('aedge:price.api.http-prices', pa('callers'), pa('price-controllers'), 'message', 'get-prices', 'Requests.', [PV3]),
 E('aedge:price.api.http-campaigns', pa('callers'), pa('campaigns'), 'message', 'Campaigns', 'Requests.', [PCAM]),
 E('aedge:price.api.http-sales', pa('callers'), pa('sales-report'), 'message', 'Price report', 'Requests.', [PSAL]),
 E('aedge:price.api.http-admin', pa('callers'), pa('admin'), 'message', 'Admin', 'DspAdmin role.', [PADM]),
 E('aedge:price.api.prices-reader', pa('price-controllers'), pa('price-reader'), 'dependency', 'IPriceReader', 'All versions.', [PV1, PV3]),
 E('aedge:price.api.campaigns-reader', pa('campaigns'), pa('price-reader'), 'dependency', 'IPriceReader', 'Campaign reads.', [PCAM]),
 E('aedge:price.api.reader-db', pa('price-reader'), pa('price-db'), 'data', 'Prices / reference data', 'DB readers.', [PRDR, PODS]),
 E('aedge:price.api.reader-erp', pa('price-reader'), pa('erp'), 'dependency', 'M3 prices', 'M3 strategies and fallbacks.', [PM3R, PERP]),
 E('aedge:price.api.admin-cmd', pa('admin'), pa('commands-out'), 'message', 'DeleteNexstepPriceList', 'Asynchronous delete.', [PADM]),
]
p_api = L('layer:price.api', 'price', 'Api \u2014 price queries and admin',
 'Inside the price Api container: versioned controllers delegate to a single price reader that chooses DSP calculation or M3 pricing per tenant; admin deletes are handed to Functions.',
 None, 'container:price.api', [PREG('price api container.'), PRDR, PADM],
 [TREE_NOTE, 'AdminController injects IIngressQueueClient (line 18) but the reviewed action only uses the price queue; no ingress edge drawn.', 'V3 PricesController also uses customer, currency, unit and cost-price readers; grouped under price reading.'],
 pan, pae)

pg = lambda s: q('get-prices.' + s)
pgn = [
 N(pg('strategy'), 'Strategy selection', 'component', 'Maps (tenant, DataSource, useDspCalculation) to a reader strategy; DSP calculation is decided by override mode and per-tenant feature flags (OptimeraSE and DahlFI always DSP).', 'inside', [PRDR]),
 N(pg('flags'), 'DSP feature management', 'external', 'Flags DisableDspPriceCalculation and EnableDspPriceCalculation{Dahl,Optimera,Flisekompaniet}.', 'outside', [PRDR], ['Per-environment flag values not recorded.']),
 N(pg('dsp-strategies'), 'DSP strategies', 'component', 'Dsp variants (Optimera, OptimeraSE, Dahl, DahlFI, Flisekompaniet) read DB data and apply customer prices via DSP calculation.', 'inside', [PODS]),
 N(pg('m3-strategies'), 'M3 API strategies', 'component', 'M3Api variants and Dahl on-prem read DB data and apply customer prices from M3.', 'inside', [POM3, PDOP]),
 N(pg('dsp-retriever'), 'DSP calculation retriever', 'component', 'Applies calculated customer and direct-delivery prices; falls back to M3 retrieval where it cannot calculate.', 'inside', [PDCR]),
 N(pg('calculator'), 'Price calculator', 'component', 'Composes resolvers for customer, item master, order-type defaults, price-list matrix/table, discount numbers/discounts, direct delivery and customer agreements.', 'inside', [PCALC, PDOC], ['docs/PriceCalculator.md may describe precalculation phases not yet in code.']),
 N(pg('m3-retriever'), 'M3 price retriever', 'component', 'Fetches customer and direct-delivery prices from the ERP price API.', 'inside', [PM3R, PDDR]),
 N(pg('price-db'), 'Price SQL (read)', 'store', 'Prices, warehouses, products and calculation reference data.', 'outside', [PRDB], owner='price'),
 N(pg('erp'), 'M3 price API', 'external', 'External ERP price endpoint.', 'outside', [PERP]),
]
pge = [
 E('aedge:price.get-prices.flags', pg('strategy'), pg('flags'), 'dependency', 'DSP calculation flags', 'Per request.', [PRDR]),
 E('aedge:price.get-prices.to-dsp', pg('strategy'), pg('dsp-strategies'), 'dependency', 'useDspCalculation = true', 'Tenant-specific.', [PRDR]),
 E('aedge:price.get-prices.to-m3', pg('strategy'), pg('m3-strategies'), 'dependency', 'useDspCalculation = false / on-prem', 'Tenant-specific.', [PRDR]),
 E('aedge:price.get-prices.dsp-retr', pg('dsp-strategies'), pg('dsp-retriever'), 'dependency', 'Customer prices', 'Calculated.', [PODS]),
 E('aedge:price.get-prices.dsp-db', pg('dsp-strategies'), pg('price-db'), 'data', 'Base prices / warehouses / products', 'DB readers.', [PODS]),
 E('aedge:price.get-prices.m3-retr', pg('m3-strategies'), pg('m3-retriever'), 'dependency', 'Customer prices', 'From M3.', [POM3, PDOP]),
 E('aedge:price.get-prices.m3-db', pg('m3-strategies'), pg('price-db'), 'data', 'Base prices / warehouses / products', 'DB readers.', [POM3]),
 E('aedge:price.get-prices.calc', pg('dsp-retriever'), pg('calculator'), 'dependency', 'IPriceCalculator', 'Calculation.', [PDCR]),
 E('aedge:price.get-prices.fallback', pg('dsp-retriever'), pg('m3-retriever'), 'dependency', 'Fallback', 'When calculation is not possible.', [PDCR]),
 E('aedge:price.get-prices.calc-db', pg('calculator'), pg('price-db'), 'data', 'Calculation reference data', 'Via resolvers.', [PCALC], ['Exact reader per resolver not traced.']),
 E('aedge:price.get-prices.erp', pg('m3-retriever'), pg('erp'), 'dependency', 'Item prices', 'ERP calls.', [PM3R, PERP]),
]
p_get = L('layer:price.api.get-prices', 'price', 'Price reading \u2014 strategy, DSP calculation and M3 fallback',
 'Second-level drill-down of Api \u2192 \u201cPrice reading\u201d: how a tenant request resolves to DSP-calculated or M3-sourced prices.',
 'layer:price.api', None, [PRDR, PDCR, PCALC, PDOC],
 [TREE_NOTE, 'Batch readers (PriceBatchReaderDsp/M3Api) are used by Api.Batch and not expanded here.'], pgn, pge)

pb = lambda s: q('api-batch.' + s)
pbn = [
 N(pb('callers'), 'HTTP batch callers', 'external', 'Batch price clients.', 'outside', [PBAT, PREG('price.api-batch.http handler.')], ['Clients not evidenced.']),
 N(pb('controllers'), 'Batch price controllers', 'component', 'prices/batch, /batch/dsp and /batch/m3api get-prices endpoints.', 'inside', [PBAT, PBATD, PBATM]),
 N(pb('reader'), 'Batch price reading', 'component', 'Price reader batch paths (DSP batch reader or M3 API batch reader).', 'inside', [PRDR, PBRD, PBRM], ['Batch strategy selection not traced line-by-line; assumed to reuse IPriceReader as the controllers inject it.']),
 N(pb('price-db'), 'Price SQL (read)', 'store', 'Read-side price database.', 'outside', [PRDB], owner='price'),
 N(pb('erp'), 'M3 price API', 'external', 'External ERP price endpoint.', 'outside', [PERP]),
]
pbe = [
 E('aedge:price.api-batch.http', pb('callers'), pb('controllers'), 'message', 'Batch get-prices', 'Requests.', [PBAT]),
 E('aedge:price.api-batch.reader', pb('controllers'), pb('reader'), 'dependency', 'IPriceReader', 'Injected.', [PBAT, PBATD]),
 E('aedge:price.api-batch.db', pb('reader'), pb('price-db'), 'data', 'Prices', 'Read.', [PBRD]),
 E('aedge:price.api-batch.erp', pb('reader'), pb('erp'), 'dependency', 'M3 prices', 'M3 batch path.', [PBRM], ['Exact ERP call path in batch reader not traced.']),
]
p_bat = L('layer:price.api-batch', 'price', 'Api.Batch \u2014 batch price queries (thin)',
 'Thin layer: batch controllers delegate to the shared price reader batch paths. Kept deliberately coarse; see uncertainties.',
 None, 'container:price.api-batch', [PREG('price api-batch container.'), PBAT],
 [TREE_NOTE, 'Thin by design: internal batch reader logic was not reviewed in depth; treat as high uncertainty.'], pbn, pbe)

def write(proj, layers):
    root = BASE / 'layers' / proj
    entries = []
    for lay in layers:
        fn = lay['id'].split(':', 1)[1].split('.', 1)[1] + '.json'
        if fn != 'ingress-app.json' or proj != 'customerproject':
            (root / fn).write_text(json.dumps(lay, indent=2, ensure_ascii=True) + '\n')
        entries.append({'id': lay['id'], 'path': fn})
    return entries
cp_entries = [{'id': 'layer:customerproject.ingress-app', 'path': 'ingress-app.json'}] + write('customerproject', [func_layer, child_layer, api_layer])
(BASE / 'layers/customerproject/index.json').write_text(json.dumps({'$schema': '../../architecture-layer.schema.json', 'schemaVersion': 1, 'documentType': 'architecture-catalog', 'project': 'customerproject', 'registrationId': 'customerproject.dev', 'layers': cp_entries}, indent=2) + '\n')
pr_entries = write('price', [p_func, p_ing, p_api, p_get, p_bat])
(BASE / 'layers/price/index.json').write_text(json.dumps({'$schema': '../../architecture-layer.schema.json', 'schemaVersion': 1, 'documentType': 'architecture-catalog', 'project': 'price', 'registrationId': 'price.dev', 'layers': pr_entries}, indent=2) + '\n')
print('wrote', [e['path'] for e in cp_entries], [e['path'] for e in pr_entries])
