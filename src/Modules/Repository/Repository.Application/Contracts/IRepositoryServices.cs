namespace SaaSApp.Repository.Application.Contracts;

public interface IRepositorySchemaService
{
    Task ApplyBaseSchemaAsync(string connectionString, CancellationToken cancellationToken = default);
}

public interface IRepositoryStorageSeedService
{
    Task EnsureDefaultProvidersAsync(Guid tenantId, Guid? createdBy, CancellationToken cancellationToken = default);
    Task EnsureDefaultProvidersAsync(string connectionString, Guid tenantId, Guid? createdBy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageProviderDto>> ListProvidersAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<Guid> ResolveStorageProviderIdAsync(Guid tenantId, Guid? storageProviderId, string? storageProviderCode, CancellationToken cancellationToken = default);
}

public interface IStaticRepositoryProvisioner
{
    Task<CreateRepositoryResult> CreateRepositoryAsync(CreateRepositoryRequest request, Guid tenantId, Guid? userId, CancellationToken cancellationToken = default);
    Task<RepositoryDetailDto?> GetRepositoryAsync(Guid repositoryId, Guid tenantId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RepositorySummaryDto>> ListRepositoriesAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<RepositoryDetailDto?> UpdateRepositoryAsync(Guid repositoryId, Guid tenantId, UpdateRepositoryRequest request, Guid? userId, CancellationToken cancellationToken = default);
    /// <summary>Creates missing per-repo tables (e.g. stage table for repos created before stage DDL existed).</summary>
    Task EnsureRepositoryTablesAsync(Guid repositoryId, Guid tenantId, CancellationToken cancellationToken = default);
}

public interface IRepositoryBrowseService
{
    Task<BrowseStructureDto> GetBrowseStructureAsync(Guid repositoryId, Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Next folder level from browse path + query filters (no field name required from UI).</summary>
    Task<BrowseChildrenResponseDto> GetBrowseChildrenAsync(
        Guid repositoryId,
        Guid tenantId,
        string pathId,
        IReadOnlyDictionary<string, string> parentFilters,
        int page,
        int pageSize,
        string? search,
        Guid? userId = null,
        bool isAdmin = false,
        CancellationToken cancellationToken = default);

    /// <summary>Group items by any folder field; parent filters via query string (e.g. ?Supplier=Acme).</summary>
    Task<PagedResult<BrowseGroupDto>> GetBrowseGroupsAsync(
        Guid repositoryId,
        Guid tenantId,
        string groupField,
        IReadOnlyDictionary<string, string> parentFilters,
        int page,
        int pageSize,
        string? search,
        Guid? userId = null,
        bool isAdmin = false,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryItemQueryService
{
    Task<ItemListFilterSchemaDto> GetItemListFilterSchemaAsync(Guid repositoryId, Guid tenantId, CancellationToken cancellationToken = default);
    Task<PagedResult<RepositoryItemListDto>> ListItemsAsync(Guid repositoryId, Guid tenantId, RepositoryItemListQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FacetValueDto>> GetFacetsAsync(
        Guid repositoryId,
        Guid tenantId,
        string fieldName,
        string? scopeFilters,
        int limit,
        CancellationToken cancellationToken = default);
    Task<RepositoryItemDetailDto?> GetItemAsync(Guid repositoryId, Guid tenantId, Guid itemId, CancellationToken cancellationToken = default);
    Task<RepositoryItemWorkspaceDto?> GetItemWorkspaceAsync(Guid repositoryId, Guid tenantId, Guid itemId, CancellationToken cancellationToken = default);
    Task<Guid> CreateItemAsync(Guid repositoryId, Guid tenantId, CreateRepositoryItemRequest request, Guid? userId, CancellationToken cancellationToken = default);
    Task<UpdateRepositoryItemMetadataResult?> UpdateItemMetadataAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        IReadOnlyDictionary<string, string> metadata,
        Guid? userId,
        CancellationToken cancellationToken = default);
    Task<RepositoryItemFileContent?> OpenItemFileAsync(Guid repositoryId, Guid tenantId, Guid itemId, CancellationToken cancellationToken = default);
}

public interface IRepositoryRelatedDocumentsService
{
    /// <summary>
    /// Related documents (loose): with 3 folder fields, any 2 matches is enough.
    /// FE passes only repositoryId + itemId.
    /// </summary>
    Task<RepositoryRelatedDocumentsResultDto?> GetRelatedAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Related documents (exact / score).
    /// If <paramref name="fields"/> is empty → match all repository fields (with values).
    /// If fields are provided → match only those fields.
    /// Optional <paramref name="value"/> overrides the source value when a single field is specified.
    /// </summary>
    Task<RepositoryRelatedDocumentsResultDto?> GetRelatedExactAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        int page = 1,
        int pageSize = 50,
        IReadOnlyList<string>? fields = null,
        string? value = null,
        CancellationToken cancellationToken = default);

    /// <summary>Saved related docs for this source item (latest replace-on-save set).</summary>
    Task<RepositorySavedRelatedDocumentsResultDto?> GetSavedRelatedAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace all saved related docs for this source item with the provided selection.
    /// </summary>
    Task<RepositorySavedRelatedDocumentsResultDto?> SaveRelatedAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        SaveRepositoryRelatedDocumentsRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryFileUploadService
{
    Task<RepositoryUploadItemResult> UploadItemAsync(
        Guid repositoryId,
        Guid tenantId,
        RepositoryUploadItemRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);
}

public interface IRepositoryItemActivityService
{
    Task<RepositoryItemTimelineResultDto?> GetTimelineAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken = default);

    Task<RepositoryItemTimelineEventDto?> AddTimelineEventAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        AddRepositoryItemTimelineEventRequest request,
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task RecordTimelineEventAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        string eventType,
        string title,
        string? description,
        string actorType,
        string? actorName,
        Guid? actorUserId,
        Guid? createdBy,
        CancellationToken cancellationToken = default);

    Task<RepositoryItemCommentsResultDto?> GetCommentsAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<AddRepositoryItemCommentResult?> AddCommentAsync(
        Guid repositoryId,
        Guid tenantId,
        Guid itemId,
        AddRepositoryItemCommentRequest request,
        Guid userId,
        CancellationToken cancellationToken = default);
}
