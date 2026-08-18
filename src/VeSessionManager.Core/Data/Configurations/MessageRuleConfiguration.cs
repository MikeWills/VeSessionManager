using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VeSessionManager.Core.Entities;

namespace VeSessionManager.Core.Data.Configurations;

/// <summary>
/// Model configuration for <see cref="MessageRule"/> (#401).
/// </summary>
public class MessageRuleConfiguration : IEntityTypeConfiguration<MessageRule>
{
    public void Configure(EntityTypeBuilder<MessageRule> b)
    {
        // Exactly the shape of every scan: this team's enabled rules for one trigger. There is no
        // other read of this table outside the admin screen, which lists a whole team's worth.
        b.HasIndex(r => new { r.TeamId, r.Trigger, r.IsEnabled });

        // Restrict, per the whole-model convention (see AppDbContext) — and the audit trail in
        // MessageRuleRun is the reason it matters here rather than being boilerplate.
        b.HasOne(r => r.Team).WithMany().HasForeignKey(r => r.TeamId).OnDelete(DeleteBehavior.Restrict);
    }
}
