using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AutoRip.Data;

public class SettingEntity
{
    [Key]
    [MaxLength(128)]
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }
}
