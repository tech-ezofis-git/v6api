-- Configure SAP BTP XSUAA OAuth provider (catalog DB).
-- Do NOT commit real ClientSecret into git. Run this locally / in a secure secret store.
--
-- OAuth redirect_uri (must match SAP BTP / xs-security redirect-uris exactly):
--   https://localhost:44311/api/connector/oauth/callback
--
-- After login, API saves RefreshToken + AccessToken on tenant dbo."connector".
-- Optional UI landing page after callback:
--   https://localhost:44311/   (via SuccessRedirectUrl on authorize request)

UPDATE catalog."ConnectorProviders"
SET
    "ClientId" = '<PASTE_CLIENT_ID>',
    "ClientSecret" = '<PASTE_CLIENT_SECRET>',
    "AuthUrl" = 'https://feb80862trial.authentication.ap21.hana.ondemand.com/oauth/authorize',
    "TokenUrl" = 'https://feb80862trial.authentication.ap21.hana.ondemand.com/oauth/token',
    "Scopes" = 'openid uaa.user',
    "RedirectUri" = 'https://localhost:44311/api/connector/oauth/callback',
    "IsActive" = true,
    "ModifiedAtUtc" = now()
WHERE "ProviderCode" = 'SAP_XSUAA';

-- If row missing (API not started yet to seed):
INSERT INTO catalog."ConnectorProviders"
    ("Id", "ProviderCode", "DisplayName", "AuthUrl", "TokenUrl", "Scopes",
     "ClientId", "ClientSecret", "RedirectUri", "IsActive", "CreatedAtUtc")
SELECT
    gen_random_uuid(),
    'SAP_XSUAA',
    'SAP BTP XSUAA',
    'https://feb80862trial.authentication.ap21.hana.ondemand.com/oauth/authorize',
    'https://feb80862trial.authentication.ap21.hana.ondemand.com/oauth/token',
    'openid uaa.user',
    '<PASTE_CLIENT_ID>',
    '<PASTE_CLIENT_SECRET>',
    'https://localhost:44311/api/connector/oauth/callback',
    true,
    now()
WHERE NOT EXISTS (
    SELECT 1 FROM catalog."ConnectorProviders" WHERE "ProviderCode" = 'SAP_XSUAA'
);
