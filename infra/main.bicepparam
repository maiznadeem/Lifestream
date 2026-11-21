using './main.bicep'

// Everything personal or secret comes from the environment of the person applying the template. Nothing here is a
// credential, and nothing in this file may ever become one: tests/infra/BicepTests.ps1 fails the build if it does.
// See infra/README.md for how each variable is filled from the local secret store.

param location = readEnvironmentVariable('LIFESTREAM_LOCATION', 'eastus')
param apiImage = readEnvironmentVariable('LIFESTREAM_API_IMAGE')

param entraAdminLogin = readEnvironmentVariable('LIFESTREAM_ENTRA_ADMIN_LOGIN')
param entraAdminObjectId = readEnvironmentVariable('LIFESTREAM_ENTRA_ADMIN_OBJECT_ID')
param ownerEmail = readEnvironmentVariable('LIFESTREAM_OWNER_EMAIL')

// Defaults to 'true' so that re-applying the template never re-enables SQL logins (INFRA-05). Only the one-time bootstrap
// (infra/README.md step 3) sets LIFESTREAM_ENTRA_ONLY=false, until the contained users and the API are proven.
param entraOnlyAuthentication = toLower(readEnvironmentVariable('LIFESTREAM_ENTRA_ONLY', 'true')) == 'true'

// 'false' only for a run that must delete something under the locked server.
param enableLocks = toLower(readEnvironmentVariable('LIFESTREAM_ENABLE_LOCKS', 'true')) == 'true'

param jwtSigningKey = readEnvironmentVariable('LIFESTREAM_JWT_SIGNING_KEY')
param ownerAdminPassword = readEnvironmentVariable('LIFESTREAM_OWNER_ADMIN_PASSWORD')
param originAuthSecret = readEnvironmentVariable('LIFESTREAM_ORIGIN_AUTH')
param originAuthMode = readEnvironmentVariable('LIFESTREAM_ORIGIN_AUTH_MODE', 'log')
