namespace Akasha.Domain.Authorization;

/// <summary>
/// Who a grant/assignment applies to. Service accounts are principals of type
/// <see cref="User"/> (they are <c>User</c> entities with <c>UserKind.ServiceAccount</c>);
/// there is no separate authorization path for them.
/// </summary>
public enum PrincipalType
{
    User = 0,
    Group = 1,
    Role = 2,
}
