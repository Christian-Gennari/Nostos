using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class NostosDbContextModelSnapshotParityTests
{
    private sealed class SubclassedMigrationDbContext(DbContextOptions<SubclassedMigrationDbContext> options)
        : NostosDbContext(options);

    [Fact]
    public void Snapshot_and_current_model_have_zero_diff_and_no_pending_changes()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                "Data Source=:memory:",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;
        using var db = new NostosDbContext(options);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "splitting entity configurations out of NostosDbContext must leave the EF Core model snapshot zero-diff");

        var migrationsAssembly = db.GetService<IMigrationsAssembly>();
        migrationsAssembly.ModelSnapshot.Should().NotBeNull();

        var initializer = db.GetService<IModelRuntimeInitializer>();
        var snapshotModel = initializer.Initialize(migrationsAssembly.ModelSnapshot!.Model);
        var designTimeModel = db.GetService<IDesignTimeModel>().Model;
        var modelDiffer = db.GetService<IMigrationsModelDiffer>();

        modelDiffer.GetDifferences(
                snapshotModel.GetRelationalModel(),
                designTimeModel.GetRelationalModel())
            .Should().BeEmpty();
    }

    [Fact]
    public void Subclassed_dbcontext_reuses_exact_same_relational_model()
    {
        var baseOptions = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var subOptions = new DbContextOptionsBuilder<SubclassedMigrationDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var baseDb = new NostosDbContext(baseOptions);
        using var subDb = new SubclassedMigrationDbContext(subOptions);

        var baseDesignTime = baseDb.GetService<IDesignTimeModel>().Model;
        var subDesignTime = subDb.GetService<IDesignTimeModel>().Model;
        var modelDiffer = baseDb.GetService<IMigrationsModelDiffer>();

        modelDiffer.GetDifferences(
                baseDesignTime.GetRelationalModel(),
                subDesignTime.GetRelationalModel())
            .Should().BeEmpty();
    }

    [Fact]
    public void Consolidated_entity_configurations_preserve_all_rules()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new NostosDbContext(options);

        var bookType = db.Model.FindEntityType(typeof(BookModel))!;
        bookType.FindDiscriminatorProperty()!.Name.Should().Be("BookType");
        bookType.FindNavigation(nameof(BookModel.Work))!.IsEagerLoaded.Should().BeTrue();
        bookType.GetIndexes().Should().Contain(i =>
            i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(BookModel.NormalizedIsbn) }));
        bookType.GetIndexes().Should().Contain(i =>
            i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(BookModel.NormalizedAsin) }));

        var writingType = db.Model.FindEntityType(typeof(WritingModel))!;
        writingType.FindProperty(nameof(WritingModel.PageSetup))!.GetMaxLength()
            .Should().Be(WritingModel.MaxPageSetupLength);
        writingType.GetForeignKeys().Should().ContainSingle(fk =>
            fk.DeleteBehavior == DeleteBehavior.Cascade
            && fk.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(WritingModel.ParentId) }));

        var topicType = db.Model.FindEntityType(typeof(TopicModel))!;
        topicType.FindProperty(nameof(TopicModel.Description))!.GetMaxLength()
            .Should().Be(TopicModel.MaxDescriptionLength);
        topicType.GetIndexes().Should().ContainSingle(i =>
            i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(TopicModel.NormalizedName) }));
    }
}
