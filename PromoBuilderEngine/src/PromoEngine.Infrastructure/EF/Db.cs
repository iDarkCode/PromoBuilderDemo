using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using PromoEngine.Domain;

namespace PromoEngine.Infrastructure.EF
{
    /// <summary>
    /// Contexto de Entity Framework Core para el motor de promociones.
    /// 
    /// Implementa el patrón Unit of Work y Repository implícito proporcionando acceso
    /// a todas las entidades del dominio con configuración optimizada para PostgreSQL.
    /// 
    /// Características principales:
    /// - Configuración específica para PostgreSQL con tipos JSONB
    /// - Índices optimizados para consultas de rendimiento crítico
    /// - Conversiones automáticas de Value Objects del dominio
    /// - Soporte para auditoría y tracking de cambios
    /// - Configuración de relaciones y constraints de integridad referencial
    /// </summary>
    public sealed class PromoEngineDbContext : DbContext
    {
        private readonly ILogger<PromoEngineDbContext>? _logger;

        #region DbSets - Entidades del Dominio

        /// <summary>
        /// Promociones del sistema - Aggregate Root principal
        /// </summary>
        public DbSet<Promotion> Promotions => Set<Promotion>();

        /// <summary>
        /// Versiones de promociones por país y configuración específica
        /// </summary>
        public DbSet<PromotionVersion> PromotionVersions => Set<PromotionVersion>();

        /// <summary>
        /// Niveles de reglas organizadas jerárquicamente dentro de promociones
        /// </summary>
        public DbSet<RuleTier> RuleTiers => Set<RuleTier>();

        /// <summary>
        /// Grupos de expresiones lógicas que componen las reglas de negocio
        /// </summary>
        public DbSet<RuleExpressionGroup> ExpressionGroups => Set<RuleExpressionGroup>();

        /// <summary>
        /// Catálogo de recompensas disponibles en el sistema
        /// </summary>
        public DbSet<Reward> Rewards => Set<Reward>();

        /// <summary>
        /// Relaciones entre promociones y recompensas disponibles
        /// </summary>
        public DbSet<PromotionReward> PromotionRewards => Set<PromotionReward>();

        /// <summary>
        /// Relaciones entre grupos de reglas y recompensas específicas
        /// </summary>
        public DbSet<RuleGroupReward> RuleGroupRewards => Set<RuleGroupReward>();

        /// <summary>
        /// Historial de recompensas otorgadas a contactos - Aggregate Root
        /// </summary>
        public DbSet<ContactReward> ContactRewards => Set<ContactReward>();

        /// <summary>
        /// Catálogo de atributos disponibles para construcción de reglas
        /// </summary>
        public DbSet<AttributeCatalog> AttributeCatalogs => Set<AttributeCatalog>();

        /// <summary>
        /// Catálogo de operadores lógicos disponibles en el sistema
        /// </summary>
        public DbSet<OperatorCatalog> OperatorCatalogs => Set<OperatorCatalog>();

        /// <summary>
        /// Relaciones de compatibilidad entre operadores y tipos de datos
        /// </summary>
        public DbSet<OperatorSupportedType> OperatorSupportedTypes => Set<OperatorSupportedType>();

        /// <summary>
        /// Mensajes del patrón Outbox para garantizar consistencia eventual
        /// </summary>
        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        #endregion

        #region Constructores

        /// <summary>
        /// Inicializa una nueva instancia del contexto de base de datos
        /// </summary>
        /// <param name="options">Opciones de configuración de Entity Framework</param>
        public PromoEngineDbContext(DbContextOptions<PromoEngineDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// Inicializa una nueva instancia del contexto con logging
        /// </summary>
        /// <param name="options">Opciones de configuración de Entity Framework</param>
        /// <param name="logger">Logger para trazabilidad de operaciones de base de datos</param>
        public PromoEngineDbContext(
            DbContextOptions<PromoEngineDbContext> options, 
            ILogger<PromoEngineDbContext> logger) : base(options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #endregion

        #region Configuración del Modelo

        /// <summary>
        /// Configura el modelo de datos aplicando todas las configuraciones de entidades,
        /// relaciones, índices y constraints necesarios para el dominio de promociones.
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo de Entity Framework</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (modelBuilder == null)
                throw new ArgumentNullException(nameof(modelBuilder));

            base.OnModelCreating(modelBuilder);

            // Aplicar configuraciones por categoría
            ConfigurePromotionEntities(modelBuilder);
            ConfigureRewardEntities(modelBuilder);
            ConfigureCatalogEntities(modelBuilder);
            ConfigureInfrastructureEntities(modelBuilder);
            ConfigureValueObjectConversions(modelBuilder);
            
            // Aplicar configuraciones desde archivos de configuración si existen
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            _logger?.LogDebug("Configuración del modelo de datos completada");
        }

        /// <summary>
        /// Configura las entidades relacionadas con promociones y reglas
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigurePromotionEntities(ModelBuilder modelBuilder)
        {
            // Configuración de Promotion (Aggregate Root)
            ConfigurePromotionEntity(modelBuilder);

            // Configuración de PromotionVersion
            ConfigurePromotionVersionEntity(modelBuilder);

            // Configuración de RuleTier
            ConfigureRuleTierEntity(modelBuilder);

            // Configuración de RuleExpressionGroup
            ConfigureRuleExpressionGroupEntity(modelBuilder);

            // Configuración de relaciones entre entidades de promoción
            ConfigurePromotionRelationships(modelBuilder);
        }

        /// <summary>
        /// Configura la entidad Promotion
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigurePromotionEntity(ModelBuilder modelBuilder)
        {
            var promotionEntity = modelBuilder.Entity<Promotion>();

            promotionEntity.ToTable("promotion", schema: "promo");
            promotionEntity.HasKey(p => p.Id);

            // Configuración de propiedades
            promotionEntity.Property(p => p.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever(); // Los GUIDs se generan en el dominio

            promotionEntity.Property(p => p.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();

            promotionEntity.Property(p => p.Timezone)
                .HasColumnName("timezone")
                .HasMaxLength(50)
                .IsRequired()
                .HasDefaultValue("Europe/Madrid");

            promotionEntity.Property(p => p.GlobalCooldownDays)
                .HasColumnName("global_cooldown_days")
                .IsRequired()
                .HasDefaultValue(0);

            promotionEntity.Property(p => p.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Índices para optimización de consultas
            promotionEntity.HasIndex(p => p.Name)
                .HasDatabaseName("ix_promotion_name");

            promotionEntity.HasIndex(p => p.CreatedAt)
                .HasDatabaseName("ix_promotion_created_at");
        }

        /// <summary>
        /// Configura la entidad PromotionVersion
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigurePromotionVersionEntity(ModelBuilder modelBuilder)
        {
            var versionEntity = modelBuilder.Entity<PromotionVersion>();

            versionEntity.ToTable("promotion_version", schema: "promo");
            versionEntity.HasKey(pv => pv.Id);

            // Configuración de propiedades básicas
            versionEntity.Property(pv => pv.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            versionEntity.Property(pv => pv.PromotionId)
                .HasColumnName("promotion_id")
                .IsRequired();

            versionEntity.Property(pv => pv.Version)
                .HasColumnName("version")
                .IsRequired();

            versionEntity.Property(pv => pv.CountryIso)
                .HasColumnName("country_iso")
                .HasMaxLength(2)
                .IsRequired();

            versionEntity.Property(pv => pv.IsDraft)
                .HasColumnName("is_draft")
                .IsRequired()
                .HasDefaultValue(true);

            // Configuración de campos JSON para PostgreSQL
            versionEntity.Property(pv => pv.WorkflowJson)
                .HasColumnName("workflow_json")
                .HasColumnType("jsonb")
                .IsRequired();

            versionEntity.Property(pv => pv.ManifestJson)
                .HasColumnName("manifest_json")
                .HasColumnType("jsonb")
                .IsRequired();

            versionEntity.Property(pv => pv.Timezone)
                .HasColumnName("timezone")
                .HasMaxLength(50)
                .IsRequired();

            versionEntity.Property(pv => pv.GlobalCooldownDays)
                .HasColumnName("global_cooldown_days")
                .IsRequired();

            versionEntity.Property(pv => pv.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Configuración de Value Object ValidityPeriod
            versionEntity.OwnsOne(pv => pv.ValidityPeriod, vpBuilder =>
            {
                vpBuilder.Property(vp => vp.ValidFromUtc)
                    .HasColumnName("valid_from_utc");

                vpBuilder.Property(vp => vp.ValidToUtc)
                    .HasColumnName("valid_to_utc");
            });

            // Índices compuestos para consultas optimizadas
            versionEntity.HasIndex(pv => new { pv.PromotionId, pv.CountryIso, pv.Version })
                .IsUnique()
                .HasDatabaseName("ix_promotion_version_unique");

            versionEntity.HasIndex(pv => new { pv.CountryIso, pv.IsDraft })
                .HasDatabaseName("ix_promotion_version_country_draft");

            versionEntity.HasIndex(pv => new { pv.PromotionId, pv.IsDraft })
                .HasDatabaseName("ix_promotion_version_promotion_draft");
        }

        /// <summary>
        /// Configura la entidad RuleTier
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureRuleTierEntity(ModelBuilder modelBuilder)
        {
            var tierEntity = modelBuilder.Entity<RuleTier>();

            tierEntity.ToTable("rule_tier", schema: "promo");
            tierEntity.HasKey(rt => rt.Id);

            tierEntity.Property(rt => rt.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            tierEntity.Property(rt => rt.PromotionId)
                .HasColumnName("promotion_id")
                .IsRequired();

            tierEntity.Property(rt => rt.TierLevel)
                .HasColumnName("tier_level")
                .IsRequired();

            tierEntity.Property(rt => rt.Order)
                .HasColumnName("order")
                .IsRequired();

            tierEntity.Property(rt => rt.CooldownDays)
                .HasColumnName("cooldown_days");

            // Índices para optimización
            tierEntity.HasIndex(rt => new { rt.PromotionId, rt.TierLevel })
                .IsUnique()
                .HasDatabaseName("ix_rule_tier_promotion_level");

            tierEntity.HasIndex(rt => new { rt.PromotionId, rt.Order })
                .HasDatabaseName("ix_rule_tier_promotion_order");
        }

        /// <summary>
        /// Configura la entidad RuleExpressionGroup
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureRuleExpressionGroupEntity(ModelBuilder modelBuilder)
        {
            var groupEntity = modelBuilder.Entity<RuleExpressionGroup>();

            groupEntity.ToTable("rule_expression_group", schema: "promo");
            groupEntity.HasKey(reg => reg.Id);

            groupEntity.Property(reg => reg.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            groupEntity.Property(reg => reg.PromotionId)
                .HasColumnName("promotion_id")
                .IsRequired();

            groupEntity.Property(reg => reg.TierId)
                .HasColumnName("tier_id")
                .IsRequired();

            groupEntity.Property(reg => reg.Order)
                .HasColumnName("order")
                .IsRequired();

            groupEntity.Property(reg => reg.ExpressionJson)
                .HasColumnName("expression_json")
                .HasColumnType("jsonb")
                .IsRequired();

            // Índices para consultas de reglas
            groupEntity.HasIndex(reg => new { reg.TierId, reg.Order })
                .IsUnique()
                .HasDatabaseName("ix_rule_expression_group_tier_order");

            groupEntity.HasIndex(reg => reg.PromotionId)
                .HasDatabaseName("ix_rule_expression_group_promotion");
        }

        /// <summary>
        /// Configura las relaciones entre entidades de promoción
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigurePromotionRelationships(ModelBuilder modelBuilder)
        {
            // Relación Promotion -> PromotionVersions (1:N)
            modelBuilder.Entity<PromotionVersion>()
                .HasOne<Promotion>()
                .WithMany(p => p.Versions)
                .HasForeignKey(pv => pv.PromotionId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_promotion_version_promotion");

            // Relación PromotionVersion -> RuleTiers (1:N)
            modelBuilder.Entity<RuleTier>()
                .HasOne<PromotionVersion>()
                .WithMany(pv => pv.RuleTiers)
                .HasForeignKey(rt => rt.PromotionId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_rule_tier_promotion");

            // Relación RuleTier -> RuleExpressionGroups (1:N)
            modelBuilder.Entity<RuleExpressionGroup>()
                .HasOne<RuleTier>()
                .WithMany(rt => rt.ExpressionGroups)
                .HasForeignKey(reg => reg.TierId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_rule_expression_group_tier");
        }

        /// <summary>
        /// Configura las entidades relacionadas con recompensas
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureRewardEntities(ModelBuilder modelBuilder)
        {
            // Configuración de Reward
            ConfigureRewardEntity(modelBuilder);

            // Configuración de ContactReward
            ConfigureContactRewardEntity(modelBuilder);

            // Configuración de entidades de relación
            ConfigureRewardRelationshipEntities(modelBuilder);
        }

        /// <summary>
        /// Configura la entidad Reward
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureRewardEntity(ModelBuilder modelBuilder)
        {
            var rewardEntity = modelBuilder.Entity<Reward>();

            rewardEntity.ToTable("reward", schema: "promo");
            rewardEntity.HasKey(r => r.Id);

            rewardEntity.Property(r => r.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            rewardEntity.Property(r => r.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();

            rewardEntity.Property(r => r.Type)
                .HasColumnName("type")
                .HasConversion<int>()
                .IsRequired();

            rewardEntity.Property(r => r.IsActive)
                .HasColumnName("is_active")
                .IsRequired()
                .HasDefaultValue(true);

            rewardEntity.Property(r => r.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Configuración de Value Object MonetaryValue
            rewardEntity.OwnsOne(r => r.Value, mvBuilder =>
            {
                mvBuilder.Property(mv => mv.Amount)
                    .HasColumnName("value_amount")
                    .HasPrecision(18, 4)
                    .IsRequired();

                mvBuilder.Property(mv => mv.Unit)
                    .HasColumnName("value_unit")
                    .HasMaxLength(10)
                    .IsRequired();
            });

            // Índices para recompensas
            rewardEntity.HasIndex(r => r.Type)
                .HasDatabaseName("ix_reward_type");

            rewardEntity.HasIndex(r => r.IsActive)
                .HasDatabaseName("ix_reward_active");

            rewardEntity.HasIndex(r => r.Name)
                .HasDatabaseName("ix_reward_name");
        }

        /// <summary>
        /// Configura la entidad ContactReward
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureContactRewardEntity(ModelBuilder modelBuilder)
        {
            var contactRewardEntity = modelBuilder.Entity<ContactReward>();

            contactRewardEntity.ToTable("contact_reward", schema: "promo");
            contactRewardEntity.HasKey(cr => cr.Id);

            contactRewardEntity.Property(cr => cr.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            contactRewardEntity.Property(cr => cr.ContactId)
                .HasColumnName("contact_id")
                .IsRequired();

            contactRewardEntity.Property(cr => cr.PromotionId)
                .HasColumnName("promotion_id")
                .IsRequired();

            contactRewardEntity.Property(cr => cr.RewardId)
                .HasColumnName("reward_id");

            contactRewardEntity.Property(cr => cr.ExpressionGroupId)
                .HasColumnName("expression_group_id");

            contactRewardEntity.Property(cr => cr.TierLevel)
                .HasColumnName("tier_level")
                .IsRequired();

            contactRewardEntity.Property(cr => cr.GrantedAt)
                .HasColumnName("granted_at")
                .IsRequired();

            contactRewardEntity.Property(cr => cr.Status)
                .HasColumnName("status")
                .HasConversion<int>()
                .IsRequired();

            contactRewardEntity.Property(cr => cr.CooldownUntil)
                .HasColumnName("cooldown_until");

            contactRewardEntity.Property(cr => cr.SourceEventId)
                .HasColumnName("source_event_id")
                .HasMaxLength(100);

            // Configuración de Value Object GrantedValue
            contactRewardEntity.OwnsOne(cr => cr.GrantedValue, gvBuilder =>
            {
                gvBuilder.Property(gv => gv.Amount)
                    .HasColumnName("granted_amount")
                    .HasPrecision(18, 4)
                    .IsRequired();

                gvBuilder.Property(gv => gv.Unit)
                    .HasColumnName("granted_unit")
                    .HasMaxLength(10)
                    .IsRequired();
            });

            // Índices críticos para rendimiento
            contactRewardEntity.HasIndex(cr => new { cr.ContactId, cr.PromotionId, cr.TierLevel, cr.GrantedAt })
                .HasDatabaseName("ix_contact_reward_performance");

            contactRewardEntity.HasIndex(cr => new { cr.ContactId, cr.PromotionId, cr.SourceEventId })
                .HasDatabaseName("ix_contact_reward_idempotency");

            contactRewardEntity.HasIndex(cr => new { cr.ContactId, cr.Status, cr.CooldownUntil })
                .HasDatabaseName("ix_contact_reward_cooldown");

            contactRewardEntity.HasIndex(cr => cr.GrantedAt)
                .HasDatabaseName("ix_contact_reward_granted_at");
        }

        /// <summary>
        /// Configura las entidades de relación para recompensas
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureRewardRelationshipEntities(ModelBuilder modelBuilder)
        {
            // PromotionReward (muchos a muchos)
            var promotionRewardEntity = modelBuilder.Entity<PromotionReward>();
            promotionRewardEntity.ToTable("promotion_reward", schema: "promo");
            promotionRewardEntity.HasKey(pr => new { pr.PromotionId, pr.RewardId });

            promotionRewardEntity.Property(pr => pr.PromotionId)
                .HasColumnName("promotion_id")
                .IsRequired();

            promotionRewardEntity.Property(pr => pr.RewardId)
                .HasColumnName("reward_id")
                .IsRequired();

            // RuleGroupReward (muchos a muchos)
            var ruleGroupRewardEntity = modelBuilder.Entity<RuleGroupReward>();
            ruleGroupRewardEntity.ToTable("rule_group_reward", schema: "promo");
            ruleGroupRewardEntity.HasKey(rgr => new { rgr.ExpressionGroupId, rgr.RewardId });

            ruleGroupRewardEntity.Property(rgr => rgr.ExpressionGroupId)
                .HasColumnName("expression_group_id")
                .IsRequired();

            ruleGroupRewardEntity.Property(rgr => rgr.RewardId)
                .HasColumnName("reward_id")
                .IsRequired();
        }

        /// <summary>
        /// Configura las entidades del catálogo del sistema
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureCatalogEntities(ModelBuilder modelBuilder)
        {
            // AttributeCatalog
            var attributeEntity = modelBuilder.Entity<AttributeCatalog>();
            attributeEntity.ToTable("attribute_catalog", schema: "catalog");
            attributeEntity.HasKey(ac => ac.Id);

            attributeEntity.Property(ac => ac.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            attributeEntity.Property(ac => ac.EntityLogicalName)
                .HasColumnName("entity_logical_name")
                .HasMaxLength(100)
                .IsRequired();

            attributeEntity.Property(ac => ac.AttributeLogicalName)
                .HasColumnName("attribute_logical_name")
                .HasMaxLength(100)
                .IsRequired();

            attributeEntity.Property(ac => ac.CanonicalName)
                .HasColumnName("canonical_name")
                .HasMaxLength(200)
                .IsRequired();

            attributeEntity.Property(ac => ac.DataType)
                .HasColumnName("data_type")
                .HasConversion<int>()
                .IsRequired();

            attributeEntity.Property(ac => ac.IsExposed)
                .HasColumnName("is_exposed")
                .IsRequired()
                .HasDefaultValue(true);

            attributeEntity.Property(ac => ac.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            // Índice único para atributos
            attributeEntity.HasIndex(ac => new { ac.EntityLogicalName, ac.AttributeLogicalName })
                .IsUnique()
                .HasDatabaseName("ix_attribute_catalog_unique");

            // OperatorCatalog
            var operatorEntity = modelBuilder.Entity<OperatorCatalog>();
            operatorEntity.ToTable("operator_catalog", schema: "catalog");
            operatorEntity.HasKey(oc => oc.Id);

            operatorEntity.Property(oc => oc.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            operatorEntity.Property(oc => oc.Code)
                .HasColumnName("code")
                .HasMaxLength(20)
                .IsRequired();

            operatorEntity.Property(oc => oc.DisplayName)
                .HasColumnName("display_name")
                .HasMaxLength(100)
                .IsRequired();

            operatorEntity.Property(oc => oc.IsActive)
                .HasColumnName("is_active")
                .IsRequired()
                .HasDefaultValue(true);

            operatorEntity.Property(oc => oc.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired()
                .HasDefaultValueSql("CURRENT_TIMESTAMP");

            operatorEntity.HasIndex(oc => oc.Code)
                .IsUnique()
                .HasDatabaseName("ix_operator_catalog_code");

            // OperatorSupportedType
            var supportedTypeEntity = modelBuilder.Entity<OperatorSupportedType>();
            supportedTypeEntity.ToTable("operator_supported_type", schema: "catalog");
            supportedTypeEntity.HasKey(ost => new { ost.OperatorId, ost.DataType });

            supportedTypeEntity.Property(ost => ost.OperatorId)
                .HasColumnName("operator_id")
                .IsRequired();

            supportedTypeEntity.Property(ost => ost.DataType)
                .HasColumnName("data_type")
                .HasConversion<int>()
                .IsRequired();

            // Relación con OperatorCatalog
            supportedTypeEntity.HasOne<OperatorCatalog>()
                .WithMany(oc => oc.SupportedTypes)
                .HasForeignKey(ost => ost.OperatorId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_operator_supported_type_operator");
        }

        /// <summary>
        /// Configura las entidades de infraestructura (Outbox, etc.)
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureInfrastructureEntities(ModelBuilder modelBuilder)
        {
            var outboxEntity = modelBuilder.Entity<OutboxMessage>();

            outboxEntity.ToTable("outbox_message", schema: "infrastructure");
            outboxEntity.HasKey(om => om.Id);

            outboxEntity.Property(om => om.Id)
                .HasColumnName("id")
                .IsRequired()
                .ValueGeneratedNever();

            outboxEntity.Property(om => om.OccurredAt)
                .HasColumnName("occurred_at")
                .IsRequired();

            outboxEntity.Property(om => om.Type)
                .HasColumnName("type")
                .HasMaxLength(100)
                .IsRequired();

            outboxEntity.Property(om => om.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();

            outboxEntity.Property(om => om.IsProcessed)
                .HasColumnName("is_processed")
                .IsRequired()
                .HasDefaultValue(false);

            outboxEntity.Property(om => om.ProcessedAt)
                .HasColumnName("processed_at");

            // Índices para procesamiento eficiente
            outboxEntity.HasIndex(om => new { om.IsProcessed, om.OccurredAt })
                .HasDatabaseName("ix_outbox_message_processing");

            outboxEntity.HasIndex(om => om.Type)
                .HasDatabaseName("ix_outbox_message_type");
        }

        /// <summary>
        /// Configura las conversiones automáticas de Value Objects
        /// </summary>
        /// <param name="modelBuilder">Constructor del modelo</param>
        private static void ConfigureValueObjectConversions(ModelBuilder modelBuilder)
        {
            // Configurar conversiones globales si es necesario
            // Por ejemplo, para enums personalizados o tipos especiales
            
            var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, DateTimeOffset>(
                v => v.ToUniversalTime(),
                v => DateTime.SpecifyKind(v.DateTime, DateTimeKind.Utc));

            // Aplicar conversión a todas las propiedades DateTimeOffset
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(dateTimeOffsetConverter);
                    }
                }
            }
        }

        #endregion

        #region Métodos de Ciclo de Vida

        /// <summary>
        /// Guarda los cambios en la base de datos con logging y manejo de errores mejorado
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Número de entidades afectadas</returns>
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var changeCount = ChangeTracker.Entries().Count(e => 
                    e.State == EntityState.Added || 
                    e.State == EntityState.Modified || 
                    e.State == EntityState.Deleted);

                _logger?.LogDebug("Guardando cambios en base de datos. Entidades afectadas: {ChangeCount}", changeCount);

                var result = await base.SaveChangesAsync(cancellationToken);

                _logger?.LogDebug("Cambios guardados exitosamente. Registros afectados: {AffectedRecords}", result);

                return result;
            }
            catch (DbUpdateConcurrencyException concurrencyEx)
            {
                _logger?.LogError(concurrencyEx, "Error de concurrencia al guardar cambios");
                throw new InvalidOperationException("Error de concurrencia al guardar los datos", concurrencyEx);
            }
            catch (DbUpdateException dbEx)
            {
                _logger?.LogError(dbEx, "Error de base de datos al guardar cambios");
                throw new InvalidOperationException("Error al guardar los datos en la base de datos", dbEx);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error inesperado al guardar cambios");
                throw;
            }
        }

        /// <summary>
        /// Configura el contexto con opciones adicionales si es necesario
        /// </summary>
        /// <param name="optionsBuilder">Constructor de opciones</param>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);

            // Configuraciones adicionales si es necesario
            if (!optionsBuilder.IsConfigured)
            {
                _logger?.LogWarning("DbContext no está configurado correctamente");
            }

            // Habilitar logging detallado en desarrollo
            #if DEBUG
            optionsBuilder.EnableSensitiveDataLogging()
                         .EnableDetailedErrors();
            #endif
        }

        #endregion

        #region Métodos de Utilidad

        /// <summary>
        /// Verifica si la base de datos puede conectarse
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>True si la conexión es exitosa</returns>
        public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var canConnect = await Database.CanConnectAsync(cancellationToken);
                _logger?.LogInformation("Verificación de conexión a base de datos: {CanConnect}", canConnect);
                return canConnect;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error verificando conexión a base de datos");
                return false;
            }
        }

        /// <summary>
        /// Aplica migraciones pendientes si existen
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Task que representa la operación</returns>
        public async Task EnsureDatabaseUpToDateAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var pendingMigrations = await Database.GetPendingMigrationsAsync(cancellationToken);
                
                if (pendingMigrations.Any())
                {
                    _logger?.LogInformation("Aplicando migraciones pendientes: {Migrations}", 
                        string.Join(", ", pendingMigrations));
                    
                    await Database.MigrateAsync(cancellationToken);
                    
                    _logger?.LogInformation("Migraciones aplicadas exitosamente");
                }
                else
                {
                    _logger?.LogDebug("No hay migraciones pendientes");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error aplicando migraciones de base de datos");
                throw;
            }
        }

        #endregion
    }
}