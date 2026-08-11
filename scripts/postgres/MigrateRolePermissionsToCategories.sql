-- Migrate users.RolePermissions from category.action keys to category-only keys -- Postgres
-- Ported from scripts/MigrateRolePermissionsToCategories.sql -- Phase 3.
-- Run against each tenant database that has custom roles. Safe to re-run: collapses
-- dotted keys and removes duplicates. This is a DATA migration, not schema -- kept
-- available for Phase 7 (existing SQL Server tenants may still have dotted-key rows
-- in data being copied over) as well as for direct use against freshly-restored data.
-- CHARINDEX('.', x) -> POSITION('.' IN x); LEFT/LOWER are the same in both dialects.

BEGIN;

-- Insert distinct category-only keys derived from legacy dotted (or already-category) values.
INSERT INTO users."RolePermissions" ("TenantId", "RoleId", "PermissionKey")
SELECT DISTINCT
    rp."TenantId",
    rp."RoleId",
    CASE
        WHEN position('.' IN rp."PermissionKey") > 0
            THEN lower(left(rp."PermissionKey", position('.' IN rp."PermissionKey") - 1))
        ELSE lower(rp."PermissionKey")
    END AS category_key
FROM users."RolePermissions" rp
WHERE NOT EXISTS (
    SELECT 1
    FROM users."RolePermissions" existing
    WHERE existing."TenantId" = rp."TenantId"
      AND existing."RoleId" = rp."RoleId"
      AND existing."PermissionKey" = CASE
            WHEN position('.' IN rp."PermissionKey") > 0
                THEN lower(left(rp."PermissionKey", position('.' IN rp."PermissionKey") - 1))
            ELSE lower(rp."PermissionKey")
        END
);

-- Remove legacy dotted keys and any non-canonical casing after the insert above.
DELETE FROM users."RolePermissions"
WHERE position('.' IN "PermissionKey") > 0
   OR "PermissionKey" <> lower("PermissionKey");

COMMIT;
