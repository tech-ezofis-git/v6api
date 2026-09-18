using SaaSApp.SharedKernel.Domain;
using SaaSApp.SharedKernel.Domain.Interfaces;
using SaaSApp.Users.Domain.Events;

namespace SaaSApp.Users.Domain.Entities;

/// <summary>User entity for a tenant. Supports Ezofis, Google, Office365 auth strategies and 2FA.</summary>
public sealed class User : Entity<Guid>, ITenantEntity
{
    public const string RoleAdmin = "Admin";
    public const string RoleTenantUser = "TenantUser";
    public const string AuthStrategyEzofis = "Ezofis";
    public const string AuthStrategyGoogle = "Google";
    public const string AuthStrategyOffice365 = "Office365";

    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string Role { get; private set; } = "TenantUser";
    public DateTime CreatedAtUtc { get; private set; }

    // Profile & identity
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public string? ProfileId { get; private set; }
    public string? PhoneNo { get; private set; }
    public string? SecondaryEmail { get; private set; }
    public string? Language { get; private set; }
    public string? CountryCode { get; private set; }

    // Organization
    public string? Department { get; private set; }
    public string? JobTitle { get; private set; }
    public Guid? ManagerId { get; private set; }
    public string? UserType { get; private set; }
    public string? EmployeeId { get; private set; }
    public string? BusinessUnit { get; private set; }
    public string? Location { get; private set; }
    public string? GroupName { get; private set; }

    // Auth
    public string? AuthStrategy { get; private set; }
    public string? LoginType { get; private set; }
    public string? LoginName { get; private set; }
    public string? PasswordHash { get; private set; }
    public string? PinHash { get; private set; }
    public string? DeviceId { get; private set; }
    public bool TwoFactorAuthentication { get; private set; }
    public string? TotpSecretEncrypted { get; private set; }
    public int? PasswordAge { get; private set; }
    public int PasswordExpiryDays { get; private set; } = 90;
    public DateTime? AccountExpiryDate { get; private set; }
    public bool ForcePasswordResetOnLogin { get; private set; }
    public string? MfaMethods { get; private set; }
    public string? GoogleSubjectId { get; private set; }
    public string? MicrosoftOid { get; private set; }

    // Profile assets
    public string? AvatarPath { get; private set; }
    public string? IdCardPath { get; private set; }
    public string? SignaturePath { get; private set; }

    // Preferences
    public string? UiPreference { get; private set; }

    /// <summary>
    /// Onboarding wizard: 0 = tenant-setup user (must complete config), 1 = completed / skip wizard.
    /// New users created inside an existing tenant are stored as 1.
    /// </summary>
    public int Configuration { get; private set; }

    // Audit
    public DateTime? ModifiedAtUtc { get; private set; }
    public Guid? CreatedBy { get; private set; }
    public Guid? ModifiedBy { get; private set; }
    public bool IsDeleted { get; private set; }

    private User() { } // EF

    private User(
        Guid id,
        Guid tenantId,
        string email,
        string displayName,
        string role,
        string? firstName,
        string? lastName,
        string? authStrategy,
        string? loginName,
        string? loginType,
        int passwordExpiryDays,
        DateTime? accountExpiryDate,
        bool forcePasswordResetOnLogin,
        string? jobTitle,
        string? employeeId,
        string? department,
        string? businessUnit,
        Guid? managerId,
        string? location,
        string? groupName,
        bool twoFactorAuthentication,
        string? mfaMethods,
        string? phoneNo,
        string? language,
        string? countryCode,
        string? avatarPath,
        string? uiPreference,
        string? secondaryEmail,
        string? userType,
        string? idCardPath,
        string? signaturePath,
        Guid? createdBy)
    {
        Id = id;
        TenantId = tenantId;
        Email = email.Trim();
        DisplayName = displayName.Trim();
        Role = role;
        FirstName = firstName?.Trim();
        LastName = lastName?.Trim();
        AuthStrategy = authStrategy;
        LoginName = string.IsNullOrWhiteSpace(loginName) ? null : loginName.Trim();
        LoginType = string.IsNullOrWhiteSpace(loginType) ? null : loginType.Trim();
        PasswordExpiryDays = passwordExpiryDays;
        AccountExpiryDate = accountExpiryDate;
        ForcePasswordResetOnLogin = forcePasswordResetOnLogin;
        JobTitle = string.IsNullOrWhiteSpace(jobTitle) ? null : jobTitle.Trim();
        EmployeeId = string.IsNullOrWhiteSpace(employeeId) ? null : employeeId.Trim();
        Department = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
        BusinessUnit = string.IsNullOrWhiteSpace(businessUnit) ? null : businessUnit.Trim();
        ManagerId = managerId;
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        TwoFactorAuthentication = twoFactorAuthentication;
        MfaMethods = string.IsNullOrWhiteSpace(mfaMethods) ? null : mfaMethods.Trim();
        PhoneNo = string.IsNullOrWhiteSpace(phoneNo) ? null : phoneNo.Trim();
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        CountryCode = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim();
        AvatarPath = string.IsNullOrWhiteSpace(avatarPath) ? null : avatarPath.Trim();
        UiPreference = string.IsNullOrWhiteSpace(uiPreference) ? null : uiPreference.Trim();
        SecondaryEmail = string.IsNullOrWhiteSpace(secondaryEmail) ? null : secondaryEmail.Trim();
        UserType = string.IsNullOrWhiteSpace(userType) ? null : userType.Trim();
        IdCardPath = string.IsNullOrWhiteSpace(idCardPath) ? null : idCardPath.Trim();
        SignaturePath = string.IsNullOrWhiteSpace(signaturePath) ? null : signaturePath.Trim();
        CreatedBy = createdBy;
        CreatedAtUtc = DateTime.UtcNow;
        RaiseDomainEvent(new UserCreatedEvent(Guid.NewGuid(), DateTime.UtcNow, id, tenantId, email, displayName));
    }

    /// <summary>Create a new user in the tenant.</summary>
    public static User Create(
        Guid tenantId,
        string email,
        string displayName,
        string role,
        string? firstName = null,
        string? lastName = null,
        string? authStrategy = null,
        string? loginName = null,
        string? loginType = null,
        int passwordExpiryDays = 90,
        DateTime? accountExpiryDate = null,
        bool forcePasswordResetOnLogin = false,
        string? jobTitle = null,
        string? employeeId = null,
        string? department = null,
        string? businessUnit = null,
        Guid? managerId = null,
        string? location = null,
        string? groupName = null,
        bool twoFactorAuthentication = false,
        string? mfaMethods = null,
        string? phoneNo = null,
        string? language = null,
        string? countryCode = null,
        string? avatarPath = null,
        string? uiPreference = null,
        string? secondaryEmail = null,
        string? userType = null,
        string? idCardPath = null,
        string? signaturePath = null,
        Guid? createdBy = null)
    {
        return new User(
            Guid.NewGuid(),
            tenantId,
            email,
            displayName,
            role,
            firstName,
            lastName,
            authStrategy ?? AuthStrategyEzofis,
            loginName,
            loginType,
            passwordExpiryDays,
            accountExpiryDate,
            forcePasswordResetOnLogin,
            jobTitle,
            employeeId,
            department,
            businessUnit,
            managerId,
            location,
            groupName,
            twoFactorAuthentication,
            mfaMethods,
            phoneNo,
            language,
            countryCode,
            avatarPath,
            uiPreference,
            secondaryEmail,
            userType,
            idCardPath,
            signaturePath,
            createdBy);
    }

    /// <summary>Update profile and extended fields. Only non-null parameters are applied.</summary>
    public void Update(
        string? displayName = null,
        string? role = null,
        string? firstName = null,
        string? lastName = null,
        string? phoneNo = null,
        string? department = null,
        string? jobTitle = null,
        string? language = null,
        string? countryCode = null,
        string? avatarPath = null,
        string? uiPreference = null,
        string? email = null,
        string? authStrategy = null,
        string? loginName = null,
        string? loginType = null,
        int? passwordExpiryDays = null,
        DateTime? accountExpiryDate = null,
        bool? forcePasswordResetOnLogin = null,
        bool? twoFactorAuthentication = null,
        string? mfaMethods = null,
        string? employeeId = null,
        string? businessUnit = null,
        Guid? managerId = null,
        bool applyManagerId = false,
        string? location = null,
        string? groupName = null,
        bool applyGroupName = false,
        string? userType = null,
        string? secondaryEmail = null,
        string? idCardPath = null,
        string? signaturePath = null,
        Guid? modifiedBy = null)
    {
        if (displayName != null) DisplayName = displayName.Trim();
        if (role != null) Role = role.Trim();
        if (firstName != null) FirstName = firstName.Trim();
        if (lastName != null) LastName = lastName.Trim();
        if (phoneNo != null) PhoneNo = phoneNo.Trim();
        if (department != null) Department = department.Trim();
        if (jobTitle != null) JobTitle = jobTitle.Trim();
        if (language != null) Language = language.Trim();
        if (countryCode != null) CountryCode = countryCode.Trim();
        if (avatarPath != null) AvatarPath = avatarPath.Trim();
        if (uiPreference != null) UiPreference = uiPreference.Trim();
        if (email != null) Email = email.Trim();
        if (authStrategy != null) AuthStrategy = authStrategy.Trim();
        if (loginName != null) LoginName = string.IsNullOrWhiteSpace(loginName) ? null : loginName.Trim();
        if (loginType != null) LoginType = string.IsNullOrWhiteSpace(loginType) ? null : loginType.Trim();
        if (passwordExpiryDays != null) PasswordExpiryDays = passwordExpiryDays.Value;
        if (accountExpiryDate != null) AccountExpiryDate = accountExpiryDate;
        if (forcePasswordResetOnLogin != null) ForcePasswordResetOnLogin = forcePasswordResetOnLogin.Value;
        if (twoFactorAuthentication != null) TwoFactorAuthentication = twoFactorAuthentication.Value;
        if (mfaMethods != null) MfaMethods = string.IsNullOrWhiteSpace(mfaMethods) ? null : mfaMethods.Trim();
        if (employeeId != null) EmployeeId = string.IsNullOrWhiteSpace(employeeId) ? null : employeeId.Trim();
        if (businessUnit != null) BusinessUnit = string.IsNullOrWhiteSpace(businessUnit) ? null : businessUnit.Trim();
        if (applyManagerId) ManagerId = managerId;
        if (location != null) Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        if (applyGroupName) GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim();
        if (userType != null) UserType = string.IsNullOrWhiteSpace(userType) ? null : userType.Trim();
        if (secondaryEmail != null) SecondaryEmail = string.IsNullOrWhiteSpace(secondaryEmail) ? null : secondaryEmail.Trim();
        if (idCardPath != null) IdCardPath = string.IsNullOrWhiteSpace(idCardPath) ? null : idCardPath.Trim();
        if (signaturePath != null) SignaturePath = string.IsNullOrWhiteSpace(signaturePath) ? null : signaturePath.Trim();
        ModifiedBy = modifiedBy;
        ModifiedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Enable 2FA by storing encrypted TOTP secret.</summary>
    public void EnableTwoFactor(string encryptedTotpSecret)
    {
        TotpSecretEncrypted = encryptedTotpSecret;
        TwoFactorAuthentication = true;
        ModifiedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Disable 2FA and clear TOTP secret.</summary>
    public void DisableTwoFactor()
    {
        TotpSecretEncrypted = null;
        TwoFactorAuthentication = false;
        ModifiedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Set password hash (BCrypt) for Ezofis login.</summary>
    public void SetPasswordHash(string hash) => PasswordHash = hash;

    /// <summary>Set login type (e.g. EZOFIS, GOOGLE, MICROSOFT).</summary>
    public void SetLoginType(string? loginType)
    {
        LoginType = string.IsNullOrWhiteSpace(loginType) ? null : loginType.Trim();
    }

    /// <summary>Marks onboarding configuration as completed (Configuration = 1).</summary>
    public void MarkConfigurationCompleted()
    {
        Configuration = 1;
        ModifiedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Set PIN hash for PIN-based login.</summary>
    public void SetPinHash(string hash) => PinHash = hash;

    /// <summary>Soft-delete the user.</summary>
    public void SoftDelete() => IsDeleted = true;

    /// <summary>Restore a soft-deleted user.</summary>
    public void Restore() => IsDeleted = false;
}
