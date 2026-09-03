using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WTT.Application.Interfaces;
using WTT.Application.Validators;
using WTT.Domain.Entities;

namespace WTT.UI.ViewModels;

public partial class TagEditViewModel : ObservableObject
{
    private readonly ITagService _tagService;
    private readonly DeviceTagValidator _validator = new();
    private readonly bool _isNew;

    public event EventHandler? OnRequestClose;

    [ObservableProperty]
    private DeviceTag _tag;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    public TagEditViewModel(ITagService tagService, DeviceTag tag, bool isNew = true)
    {
        _tagService = tagService;
        _tag = tag;
        _isNew = isNew;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = string.Empty;

        var validationResult = await _validator.ValidateAsync(Tag);
        if (!validationResult.IsValid)
        {
            ErrorMessage = string.Join("\n", validationResult.Errors.Select(e => e.ErrorMessage));
            return;
        }

        try
        {
            if (_isNew)
            {
                await _tagService.AddTagAsync(Tag);
            }
            else
            {
                await _tagService.UpdateTagAsync(Tag);
            }

            OnRequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Error saving tag: {ex.Message}";
        }
    }
}
