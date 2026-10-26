using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using ProtoSim.Core;

namespace ProtoSim.App.Controls;

/// <summary>One editable property of a settings object, built by reflection.</summary>
public sealed class PropertyItem : ObservableObject, INotifyDataErrorInfo
{
    private readonly object _target;
    private readonly PropertyInfo _property;
    private string? _error;
    private string? _pendingText;

    public PropertyItem(object target, PropertyInfo property)
    {
        _target = target;
        _property = property;
        var display = property.GetCustomAttribute<DisplayAttribute>();
        DisplayName = display?.GetName() ?? SplitCamelCase(property.Name);
        Description = display?.GetDescription() ?? string.Empty;
        Category = display?.GetGroupName() ?? "General";
        Order = display?.GetOrder() ?? 1000;

        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        IsBool = type == typeof(bool);
        IsEnum = type.IsEnum;
        IsText = !IsBool && !IsEnum;
        EnumValues = IsEnum ? Enum.GetValues(type) : null;
    }

    public string DisplayName { get; }
    public string Description { get; }
    public string Category { get; }
    public int Order { get; }
    public bool IsBool { get; }
    public bool IsEnum { get; }
    public bool IsText { get; }
    public IEnumerable? EnumValues { get; }

    /// <summary>Raised after a value is successfully written to the target.</summary>
    public event EventHandler? ValueChanged;

    public object? Value
    {
        get => _property.GetValue(_target);
        set
        {
            if (TrySet(value)) OnPropertyChanged();
        }
    }

    public string Text
    {
        get => _pendingText ?? Convert.ToString(_property.GetValue(_target), CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            try
            {
                var converter = TypeDescriptor.GetConverter(_property.PropertyType);
                var parsed = converter.ConvertFromInvariantString(value ?? string.Empty);
                if (TrySet(parsed))
                {
                    _pendingText = null;
                }
                else
                {
                    _pendingText = value;
                }
            }
            catch (Exception)
            {
                _pendingText = value;
                SetError($"'{value}' is not a valid {_property.PropertyType.Name}.");
            }
            OnPropertyChanged();
        }
    }

    private bool TrySet(object? value)
    {
        var results = new List<ValidationResult>();
        var ctx = new ValidationContext(_target) { MemberName = _property.Name };
        if (!Validator.TryValidateProperty(value, ctx, results))
        {
            SetError(results.FirstOrDefault()?.ErrorMessage ?? "Invalid value.");
            return false;
        }
        _property.SetValue(_target, value);
        SetError(null);
        ValueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void SetError(string? error)
    {
        if (_error == error) return;
        _error = error;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(nameof(Text)));
        OnPropertyChanged(nameof(HasErrors));
    }

    public bool HasErrors => _error != null;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) => _error == null ? Array.Empty<string>() : new[] { _error };

    public static IReadOnlyList<PropertyItem> For(object? target)
    {
        if (target == null) return [];
        return target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Select(p => new PropertyItem(target, p))
            .OrderBy(p => p.Order)
            .ToList();
    }

    private static string SplitCamelCase(string s) =>
        string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(s[i - 1]) ? " " + c : c.ToString()));
}
