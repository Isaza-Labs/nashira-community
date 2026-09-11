using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace nashira_backend.Data.Db.Migrations
{
    /// <summary>
    /// Secrets become named rows instead of (provider, setting_key) pairs, and the
    /// reference grammar grows a source segment: `${secret:provider:key}` becomes
    /// `${secret:secret:&lt;name&gt;:value}`, alongside credential / ai_provider /
    /// integration / session sources that resolve without a row here at all.
    ///
    /// The scaffolded version of this migration renamed SettingKey to Name and
    /// DisplayName to CreatedBy and stopped there. Both are wrong in a way that only
    /// shows up in production: two providers sharing a key ("token") would collide on
    /// the new unique index, and every stored `${secret:…}` reference would keep
    /// naming a provider/key pair that no longer exists — resolving to a literal
    /// marker sent as a bearer token. So the conversion is written out by hand:
    /// names are derived from provider + key, deduplicated, and every reference
    /// already stored in an integration, a spec or an inventory source is rewritten
    /// to match, in the same transaction that changes the table.
    /// </summary>
    public partial class Phase11_SecretsByName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1. Derive the new name into a scratch column, while Provider still exists.
            //
            // "netbox" + "api_token" → "netbox_api_token". Anything outside the name
            // grammar becomes a hyphen; the grammar is what the controller enforces on
            // create, and a legacy row that cannot satisfy it is still resolvable, but
            // an admin editing it should not have to guess why it will not save.
            migrationBuilder.Sql("""
                ALTER TABLE secrets ADD COLUMN "NewName" text;
                UPDATE secrets
                   SET "NewName" = left(
                         lower(regexp_replace("Provider" || '_' || "SettingKey", '[^A-Za-z0-9_-]', '-', 'g')),
                         64);
                """);

            // Two rows can normalize to the same name (one provider "a" key "b_c",
            // another "a_b" key "c"). The unique index would reject the second one and
            // take the whole migration down with it, so collisions get a short suffix
            // from the row's own id — stable, and visible to whoever has to reconcile it.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "SecretId", "NewName",
                           row_number() OVER (PARTITION BY "NewName" ORDER BY "CreatedAt", "SecretId") AS rn
                      FROM secrets
                )
                UPDATE secrets s
                   SET "NewName" = left(s."NewName", 55) || '-' || left(replace(s."SecretId"::text, '-', ''), 8)
                  FROM ranked r
                 WHERE r."SecretId" = s."SecretId" AND r.rn > 1;
                """);

            // ── 2. Rewrite every reference that names a row in this table.
            //
            // Driven by the rows themselves rather than by a regex over the text: the
            // replacement is a literal string match, so a provider or key containing a
            // regex metacharacter cannot corrupt the config it appears in. Loops over
            // the secrets, not over the configs, because there are always fewer secrets.
            //
            // `${secret:session:current:jwt}` is untouched by construction — it carries
            // three segments, and nothing here matches a reference with a colon inside
            // the provider or key it is looking for.
            migrationBuilder.Sql("""
                DO $$
                DECLARE r RECORD;
                        old_ref text;
                        new_ref text;
                BEGIN
                    FOR r IN SELECT "Provider", "SettingKey", "NewName" FROM secrets LOOP
                        old_ref := '${secret:' || r."Provider" || ':' || r."SettingKey" || '}';
                        new_ref := '${secret:secret:' || r."NewName" || ':value}';

                        UPDATE integrations
                           SET "AuthConfig" = replace("AuthConfig", old_ref, new_ref)
                         WHERE "AuthConfig" IS NOT NULL AND strpos("AuthConfig", old_ref) > 0;

                        UPDATE ai_api_specs
                           SET "AuthConfig" = replace("AuthConfig", old_ref, new_ref)
                         WHERE "AuthConfig" IS NOT NULL AND strpos("AuthConfig", old_ref) > 0;

                        UPDATE inventory_sources
                           SET "TokenSecretRef" = replace("TokenSecretRef", old_ref, new_ref)
                         WHERE "TokenSecretRef" IS NOT NULL AND strpos("TokenSecretRef", old_ref) > 0;
                    END LOOP;
                END $$;
                """);

            // ── 3. Fold DisplayName into Description before dropping it.
            //
            // Description is the only prose the new shape carries, and DisplayName was
            // usually the more informative of the two. Losing it silently would delete
            // the one field explaining what a rotated token is actually for.
            migrationBuilder.Sql("""
                UPDATE secrets
                   SET "Description" = COALESCE(NULLIF("Description", ''), "DisplayName")
                 WHERE "DisplayName" IS NOT NULL AND "DisplayName" <> '';
                """);

            // ── 4. Reshape the table.
            migrationBuilder.DropIndex(
                name: "IX_secrets_Provider_SettingKey",
                table: "secrets");

            migrationBuilder.DropColumn(name: "Category", table: "secrets");
            migrationBuilder.DropColumn(name: "DisplayOrder", table: "secrets");
            migrationBuilder.DropColumn(name: "IsSecret", table: "secrets");
            migrationBuilder.DropColumn(name: "Provider", table: "secrets");
            migrationBuilder.DropColumn(name: "DisplayName", table: "secrets");
            migrationBuilder.DropColumn(name: "SettingKey", table: "secrets");

            migrationBuilder.RenameColumn(
                name: "NewName",
                table: "secrets",
                newName: "Name");

            // Spelled out rather than left to AlterColumn: the scratch column was added
            // by raw SQL, so EF believes "Name" was already NOT NULL and emits only the
            // default — leaving the column nullable underneath a model that says it is
            // not, which surfaces later as a snapshot that never stops drifting.
            migrationBuilder.Sql("""
                ALTER TABLE secrets ALTER COLUMN "Name" SET NOT NULL;
                ALTER TABLE secrets ALTER COLUMN "Name" SET DEFAULT '';
                """);

            // Nobody recorded an author before this migration, and inventing one would
            // put a name against rows that person never touched.
            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "secrets",
                type: "text",
                nullable: true);

            // A cleared secret was NULL; the new shape spells "no value" as an empty
            // array, which is what `has_value` reads. AlterColumn backfills the NULLs
            // itself, given the default.
            migrationBuilder.AlterColumn<byte[]>(
                name: "EncryptedValue",
                table: "secrets",
                type: "bytea",
                nullable: false,
                defaultValue: new byte[0],
                oldClrType: typeof(byte[]),
                oldType: "bytea",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_secrets_Name",
                table: "secrets",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_secrets_Name",
                table: "secrets");

            migrationBuilder.DropColumn(name: "CreatedBy", table: "secrets");

            migrationBuilder.AlterColumn<byte[]>(
                name: "EncryptedValue",
                table: "secrets",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "secrets",
                type: "text",
                nullable: false,
                defaultValue: "secret");

            migrationBuilder.AddColumn<int>(
                name: "DisplayOrder",
                table: "secrets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsSecret",
                table: "secrets",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "secrets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "secrets",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "secrets",
                newName: "SettingKey");

            // Split the name back at the first separator. It will not always land where
            // it was — "netbox_api_token" could have been ("netbox", "api_token") or
            // ("netbox_api", "token") — which is exactly why the forward direction
            // rewrites the references rather than trusting a reversible encoding.
            migrationBuilder.Sql("""
                UPDATE secrets
                   SET "Provider" = split_part("SettingKey", '_', 1),
                       "SettingKey" = COALESCE(NULLIF(substr("SettingKey", strpos("SettingKey", '_') + 1), ''), "SettingKey")
                 WHERE strpos("SettingKey", '_') > 0;
                UPDATE secrets SET "Provider" = "SettingKey" WHERE "Provider" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_secrets_Provider_SettingKey",
                table: "secrets",
                columns: new[] { "Provider", "SettingKey" },
                unique: true);
        }
    }
}
