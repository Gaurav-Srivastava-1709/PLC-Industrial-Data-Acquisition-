using FluentValidation;
using WTT.Domain.Entities;

namespace WTT.Application.Validators;

/// <summary>
/// DeviceTagValidator: FluentValidation rules for PLC tags.
/// </summary>
public class DeviceTagValidator : AbstractValidator<DeviceTag>
{
    public DeviceTagValidator()
    {
        RuleFor(t => t.TagName)
            .NotEmpty().WithMessage("Tag Name is required.")
            .MaximumLength(100).WithMessage("Tag Name cannot exceed 100 characters.");

        RuleFor(t => t.Address)
            .NotEmpty().WithMessage("PLC Address is required.")
            .MaximumLength(100).WithMessage("PLC Address cannot exceed 100 characters.");

        RuleFor(t => t.DataType)
            .NotEmpty().WithMessage("Data Type is required.");

        RuleFor(t => t.DeviceId)
            .GreaterThan(0).WithMessage("Valid PLC Device must be selected.");
    }
}
