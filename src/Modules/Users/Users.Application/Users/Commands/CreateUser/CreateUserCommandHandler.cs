using MediatR;
using SaaSApp.Users.Application.Contracts;
using SaaSApp.Users.Domain.Entities;

namespace SaaSApp.Users.Application.Users.Commands.CreateUser;

public sealed class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, CreateUserCommandResult>
{
    private readonly IUserRepository _userRepository;
    private readonly IGroupRepository _groupRepository;
    private readonly IUsersSchemaEnsurer _usersSchemaEnsurer;
    private readonly IBuiltinRoleProvisioning _builtinRoleProvisioning;
    private readonly ITenantContext _tenantContext;

    public CreateUserCommandHandler(
        IUserRepository userRepository,
        IGroupRepository groupRepository,
        IUsersSchemaEnsurer usersSchemaEnsurer,
        IBuiltinRoleProvisioning builtinRoleProvisioning,
        ITenantContext tenantContext)
    {
        _userRepository = userRepository;
        _groupRepository = groupRepository;
        _usersSchemaEnsurer = usersSchemaEnsurer;
        _builtinRoleProvisioning = builtinRoleProvisioning;
        _tenantContext = tenantContext;
    }

    public async Task<CreateUserCommandResult> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.TenantId
            ?? throw new InvalidOperationException("TenantId is required to create a user.");

        if (string.IsNullOrWhiteSpace(request.Email))
            return Fail("email is required.");

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return Fail("displayName is required.");

        if (UserCreateValidation.TryParseYesNo(request.ForcePasswordResetOnLogin, out var forcePasswordResetOnLogin) != true)
            return Fail("forcePasswordResetOnLogin is required and must be Yes or No.");

        if (UserCreateValidation.TryParseYesNo(request.MfAuthentication, out var twoFactorAuthentication) != true)
            return Fail("MFAuthentication is required and must be Yes or No.");

        var loginType = UserCreateValidation.ResolveLoginType(request.LoginType);
        if (!UserCreateValidation.IsAllowedLoginType(loginType))
            return Fail($"LoginType must be one of: Password, GoogleSSO, MS Entra ID, LDAP/AD.");

        if (!string.IsNullOrWhiteSpace(request.MfaMethods))
        {
            var mfaMethod = request.MfaMethods.Trim();
            if (!UserCreateValidation.IsAllowedMfaMethod(mfaMethod))
                return Fail("MFA Methods must be one of: Email OTP, Mobile OTP, Authenticator OTP.");
        }

        var resolvedRole = UserCreateValidation.ResolveRole(request.Role);
        var groupNames = UserCreateValidation.NormalizeGroupNames(request.Groups);

        if (groupNames.Count > 0)
            await _usersSchemaEnsurer.EnsureGroupsTablesAsync(cancellationToken);

        var groupNameValue = UserCreateValidation.FormatGroupNamesForStorage(groupNames);
        if (groupNameValue?.Length > UserCreateValidation.MaxGroupNameLength)
            return Fail("Combined group names exceed 128 characters.");

        var passwordExpiryDays = UserCreateValidation.ResolvePasswordExpiryDays(request.PasswordExpiryDays);
        var accountExpiryDate = UserCreateValidation.ResolveAccountExpiryDate(
            request.AccountExpiryDate,
            passwordExpiryDays,
            out var accountExpiryError);
        if (accountExpiryError != null)
            return Fail(accountExpiryError);

        if (loginType == UserCreateValidation.LoginTypePassword && string.IsNullOrWhiteSpace(request.Password))
            return Fail("password is required when LoginType is Password.");

        if (!string.IsNullOrWhiteSpace(request.SecondaryEmail)
            && !UserCreateValidation.IsValidEmail(request.SecondaryEmail))
            return Fail("secondaryEmail is not a valid email address.");

        Guid? managerId = null;
        if (!string.IsNullOrWhiteSpace(request.Manager))
        {
            var manager = await _userRepository.FindByEmailOrDisplayNameAsync(request.Manager, cancellationToken);
            if (manager == null)
                return Fail($"Manager '{request.Manager.Trim()}' was not found.");
            managerId = manager.Id;
        }

        var email = request.Email.Trim();
        if (await _userRepository.GetByEmailAsync(email, cancellationToken) != null)
            return Fail("A user with this email already exists.");

        var user = User.Create(
            tenantId,
            email,
            request.DisplayName,
            resolvedRole,
            request.FirstName,
            request.LastName,
            UserCreateValidation.ResolveAuthStrategy(request.AuthStrategy),
            request.UserName,
            loginType,
            passwordExpiryDays,
            accountExpiryDate,
            forcePasswordResetOnLogin,
            request.JobTitle,
            request.EmployeeId,
            request.Department,
            request.BusinessUnit,
            managerId,
            request.Location,
            groupNameValue,
            twoFactorAuthentication,
            request.MfaMethods,
            request.PhoneNo,
            request.Language,
            request.CountryCode,
            request.AvatarPath,
            request.UiPreference,
            request.SecondaryEmail,
            request.UserType,
            request.IdCardPath,
            request.SignaturePath,
            request.CreatedBy);

        if (!string.IsNullOrWhiteSpace(request.Password))
            user.SetPasswordHash(BCrypt.Net.BCrypt.HashPassword(request.Password.Trim()));

        // Tenant-setup users stay Configuration=0 (onboarding wizard). Users created later skip it.
        user.MarkConfigurationCompleted();

        await _userRepository.AddAsync(user, cancellationToken);

        foreach (var groupName in groupNames)
        {
            var group = await _groupRepository.GetByNameAsync(groupName, cancellationToken);
            if (group == null)
            {
                group = Group.Create(tenantId, groupName);
                group.AssignUsers([user.Id]);
                await _groupRepository.AddAsync(group, cancellationToken);
            }
            else
            {
                await _groupRepository.AddMemberAsync(group.Id, user.Id, cancellationToken);
            }
        }

        await _builtinRoleProvisioning.SyncUserMembershipAsync(user.Id, resolvedRole, cancellationToken);

        return new CreateUserCommandResult(
            Success: true,
            UserId: user.Id,
            RoleName: resolvedRole,
            StatusCode: 201);
    }

    private static CreateUserCommandResult Fail(string error, int statusCode = 400) =>
        new(Success: false, Error: error, StatusCode: statusCode);
}
