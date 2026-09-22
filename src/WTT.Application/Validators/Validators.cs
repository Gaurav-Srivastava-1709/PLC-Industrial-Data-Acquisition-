using System.Net;
using System.Text.RegularExpressions;
using FluentValidation;
using WTT.Application.Catalog;
using WTT.Application.DTOs;
using WTT.Application.Options;
using WTT.Domain.Entities;
using WTT.Domain.Enums;

namespace WTT.Application.Validators;

public sealed partial class DeviceValidator : AbstractValidator<Device>
{
    public DeviceValidator()
    {
        RuleFor(d => d.Name)
            .NotEmpty().WithMessage("Device name is required.")
            .MaximumLength(100)
            .Matches(@"^[A-Za-z0-9_\-\. ]+$").WithMessage("Device name may only contain letters, digits, space, '_', '-' and '.'.");

        RuleFor(d => d.Host)
            .NotEmpty().WithMessage("IP address / host name is required.")
            .Must(BeValidHost).WithMessage("Enter a valid IPv4 address or host name.");

        RuleFor(d => d.Port).InclusiveBetween(1, 65535);
        RuleFor(d => d.PollIntervalMs).InclusiveBetween(100, 3_600_000).WithMessage("Poll interval must be between 100 ms and 1 hour.");
        RuleFor(d => d.TimeoutMs).InclusiveBetween(200, 60_000);
        RuleFor(d => d.Slot).InclusiveBetween(0, 31);
        RuleFor(d => d.Rack).InclusiveBetween(0, 7);
        RuleFor(d => d.ConnectionSize).Must(v => v is 511 or 4002 or 4000 or 4064).WithMessage("Connection size must be 511 or 4002.");

        RuleFor(d => d.Model)
            .Must((d, m) => PlcModelCatalog.Models.Any(x => x.Model == m && x.Protocol == d.Protocol && x.Manufacturer == d.Manufacturer))
            .WithMessage("Selected model does not belong to the chosen protocol / manufacturer.");

        When(d => d.CaptureMode == CaptureMode.PlcTrigger, () =>
            RuleFor(d => d.TriggerTagName).NotEmpty().WithMessage("Select a trigger tag for PLC Trigger mode."));
    }

    private static bool BeValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        return HostNameRegex().IsMatch(host);
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostNameRegex();
}

public sealed class DeviceTagValidator : AbstractValidator<DeviceTag>
{
    public DeviceTagValidator()
    {
        RuleFor(t => t.Name)
            .NotEmpty().WithMessage("Tag name is required.")
            .MaximumLength(200)
            .Must(n => !n.Contains('\'') && !n.Contains(',')).WithMessage("Tag name cannot contain quotes or commas.");

        RuleFor(t => t.Alias).MaximumLength(200);
        RuleFor(t => t.ElementCount).InclusiveBetween(1, 4096);
        RuleFor(t => t.DecimalPlaces).InclusiveBetween(0, 10).When(t => t.DecimalPlaces.HasValue);
        RuleFor(t => t.MinimumWriteIntervalMs).InclusiveBetween(250, 60000);
        RuleFor(t => t).Must(t => !t.WriteMinimum.HasValue || !t.WriteMaximum.HasValue || t.WriteMinimum <= t.WriteMaximum)
            .WithMessage("Write minimum cannot exceed write maximum.");

        When(t => t.Kind == TagKind.Plc, () =>
            RuleFor(t => t.PlcAddress).NotEmpty().WithMessage("PLC address is required for PLC tags."));

        When(t => t.Kind == TagKind.External, () =>
        {
            RuleFor(t => t.QueryId).NotNull().WithMessage("Select an external query.");
            RuleFor(t => t.QueryColumnId).NotNull().WithMessage("Select a query column.");
        });

        When(t => t.IsAlarmEnabled, () =>
        {
            RuleFor(t => t.AlarmDeadband).InclusiveBetween(0.0, 50.0).WithMessage("Deadband must be between 0% and 50%.");
            RuleFor(t => t.AlarmOnDelaySeconds).GreaterThanOrEqualTo(0);
            RuleFor(t => t.AlarmOffDelaySeconds).GreaterThanOrEqualTo(0);
            RuleFor(t => t).Must(t => !t.AlarmMinThreshold.HasValue || !t.AlarmMaxThreshold.HasValue || t.AlarmMinThreshold < t.AlarmMaxThreshold)
                .WithMessage("Min threshold must be strictly less than Max threshold.");
            RuleFor(t => t).Must(t => t.AlarmMinThreshold.HasValue || t.AlarmMaxThreshold.HasValue)
                .WithMessage("At least one threshold (Min or Max) must be set when alarm is enabled.");
        });
    }
}

public sealed class DeviceAddressValidator : AbstractValidator<DeviceAddress>
{
    public DeviceAddressValidator()
    {
        RuleFor(a => a.StartAddress).InclusiveBetween(0, 65535);
        RuleFor(a => a.ModuleOffset).InclusiveBetween(-65535, 65535);
        When(a => a.AddressType == ModbusAddressType.Sequential, () =>
        {
            RuleFor(a => a.PointCount).InclusiveBetween(1, 2000).WithMessage("Point count must be 1..2000.");
            RuleFor(a => a.TagPrefix).NotEmpty().WithMessage("Tag prefix is required for sequential blocks.").MaximumLength(50);
        });
    }
}

public sealed class TriggerConditionValidator : AbstractValidator<DeviceTriggerCondition>
{
    public TriggerConditionValidator()
    {
        RuleFor(c => c.TagName).NotEmpty().WithMessage("Select a tag.");
        RuleFor(c => c.Value).NotEmpty().When(c => c.Operator != TriggerOperator.OnChange).WithMessage("Enter a comparison value.");
    }
}

public sealed class DeviceServerValidator : AbstractValidator<DeviceServer>
{
    public DeviceServerValidator()
    {
        RuleFor(s => s.ServerName).NotEmpty().WithMessage("Server name is required.").MaximumLength(200);
        RuleFor(s => s.DatabaseName).NotEmpty().WithMessage("Database name is required.").MaximumLength(128);
        RuleFor(s => s.TableName).NotEmpty().Matches(@"^[A-Za-z_][A-Za-z0-9_]*$").WithMessage("Table name must be a plain identifier.");
        RuleFor(s => s.UserId).NotEmpty().When(s => !s.UseIntegratedSecurity).WithMessage("User id is required unless integrated security is used.");
    }
}

public sealed class DeviceQueryValidator : AbstractValidator<DeviceQuery>
{
    public DeviceQueryValidator()
    {
        RuleFor(q => q.Name).NotEmpty().MaximumLength(100);
        RuleFor(q => q.ServerName).NotEmpty();
        RuleFor(q => q.DatabaseName).NotEmpty();
        RuleFor(q => q.UserId).NotEmpty().When(q => !q.UseIntegratedSecurity);
        RuleFor(q => q.QueryText)
            .NotEmpty()
            .MaximumLength(8000);
    }
}

public sealed class PasswordPolicyValidator : AbstractValidator<string>
{
    public PasswordPolicyValidator(SecurityOptions options)
    {
        RuleFor(p => p)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(options.MinPasswordLength).WithMessage($"Password must be at least {options.MinPasswordLength} characters.")
            .MaximumLength(128);

        if (options.RequireMixedCasePassword)
            RuleFor(p => p).Must(p => p.Any(char.IsUpper) && p.Any(char.IsLower)).WithMessage("Password must contain upper and lower case letters.");

        if (options.RequireDigitPassword)
            RuleFor(p => p).Must(p => p.Any(char.IsDigit)).WithMessage("Password must contain a digit.");
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator(SecurityOptions options)
    {
        RuleFor(r => r.Username)
            .NotEmpty().WithMessage("Username is required.")
            .Length(3, 50)
            .Matches(@"^[A-Za-z0-9_.\-]+$").WithMessage("Username may contain letters, digits, '.', '_' and '-'.");
        RuleFor(r => r.FullName).NotEmpty().WithMessage("Full name is required.").MaximumLength(100);
        RuleFor(r => r.Password).SetValidator(new PasswordPolicyValidator(options));
    }
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Username).NotEmpty().WithMessage("Enter your username.");
        RuleFor(r => r.Password).NotEmpty().WithMessage("Enter your password.");
    }
}
