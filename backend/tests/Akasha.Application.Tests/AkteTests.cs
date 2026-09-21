using Akasha.Domain.Akten;

namespace Akasha.Application.Tests;

public sealed class AkteTests
{
    [Fact]
    public void Akte_Has_Server_Generated_Identity_And_Starts_Active()
    {
        var akte = new Akte(Guid.NewGuid(), "Customer ABC", "All contracts");

        Assert.NotEqual(Guid.Empty, akte.Id);
        Assert.NotEqual(akte.TenantId, akte.Id);
        Assert.Equal(AkteStatus.Active, akte.Status);
        Assert.False(akte.IsArchived);
        Assert.Equal("Customer ABC", akte.Name);
        Assert.Equal("All contracts", akte.Description);
        Assert.True(akte.CreatedAtUtc <= akte.UpdatedAtUtc);
    }

    [Fact]
    public void Constructor_Requires_Tenant_And_Name()
    {
        Assert.Throws<ArgumentException>(() => new Akte(Guid.Empty, "x"));
        Assert.Throws<ArgumentException>(() => new Akte(Guid.NewGuid(), ""));
        Assert.Throws<ArgumentException>(() => new Akte(Guid.NewGuid(), "   "));
        Assert.Throws<ArgumentException>(() => new Akte(Guid.NewGuid(), new string('x', Akte.MaxNameLength + 1)));
        Assert.Throws<ArgumentException>(
            () => new Akte(Guid.NewGuid(), "ok", new string('x', Akte.MaxDescriptionLength + 1)));
    }

    [Fact]
    public void Update_Changes_Name_And_Description()
    {
        var akte = new Akte(Guid.NewGuid(), "Old", null);

        akte.Update("New", "desc");

        Assert.Equal("New", akte.Name);
        Assert.Equal("desc", akte.Description);
        Assert.True(akte.UpdatedAtUtc >= akte.CreatedAtUtc);
    }

    [Fact]
    public void Archive_And_Reactivate_Are_Idempotent()
    {
        var akte = new Akte(Guid.NewGuid(), "Case");

        akte.Archive();
        Assert.Equal(AkteStatus.Archived, akte.Status);
        akte.Archive(); // no-op, still archived
        Assert.Equal(AkteStatus.Archived, akte.Status);

        akte.Reactivate();
        Assert.Equal(AkteStatus.Active, akte.Status);
        akte.Reactivate(); // no-op, still active
        Assert.Equal(AkteStatus.Active, akte.Status);
    }

    [Fact]
    public void Archived_Akte_Refuses_Update_And_Membership_Changes()
    {
        var akte = new Akte(Guid.NewGuid(), "Case");
        akte.Archive();

        Assert.Throws<InvalidOperationException>(() => akte.Update("other", null));
        Assert.Throws<InvalidOperationException>(() => akte.EnsureActive());

        // after reactivation changes are possible again
        akte.Reactivate();
        akte.Update("other", null);
        Assert.Equal("other", akte.Name);
    }

    [Fact]
    public void AkteDocument_References_Logical_Identities_Only()
    {
        var tenantId = Guid.NewGuid();
        var membership = new AkteDocument(tenantId, Guid.NewGuid(), Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, membership.Id);
        Assert.Equal(tenantId, membership.TenantId);
        Assert.NotEqual(membership.AkteId, membership.DocumentId);

        Assert.Throws<ArgumentException>(() => new AkteDocument(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new AkteDocument(tenantId, Guid.Empty, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new AkteDocument(tenantId, Guid.NewGuid(), Guid.Empty));
    }
}
