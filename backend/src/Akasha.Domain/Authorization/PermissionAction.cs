namespace Akasha.Domain.Authorization;

public enum PermissionAction
{
    Read = 0,
    Write = 1,
    Delete = 2,
    Download = 3,
    Upload = 4,
    Move = 5,
    Copy = 6,
    Rename = 7,
    Share = 8,
    EditMetadata = 9,
    StartWorkflow = 10,
    Approve = 11,
    Admin = 12,
}
