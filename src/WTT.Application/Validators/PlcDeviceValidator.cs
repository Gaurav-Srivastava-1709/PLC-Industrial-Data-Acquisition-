using FluentValidation;
using WTT.Domain.Entities;

namespace WTT.Application.Validators;

public class PlcDeviceValidator : AbstractValidator<PlcDevice>
{
    public PlcDeviceValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Device Name is required.")
            .MaximumLength(100).WithMessage("Device Name cannot exceed 100 characters.");

        RuleFor(x => x.IpAddress)
            .NotEmpty().WithMessage("IP Address is required.")
            .Matches(@"^(?:[0-9]{1,3}\.){3}[0-9]{1,3}$").WithMessage("IP Address is not in a valid format.");

        RuleFor(x => x.Port)
            .InclusiveBetween(1, 65535).WithMessage("Port must be between 1 and 65535.");

        RuleFor(x => x.Protocol)
            .NotEmpty().WithMessage("Protocol is required.");
    }
}
