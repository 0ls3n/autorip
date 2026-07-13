using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AutoRip.Models;

namespace AutoRip.Data;

[Table("RipLogs")]
public class RipLogEntry
{
    [Key]
    public long Id { get; set; }

    [MaxLength(36)]
    public string RipJobId { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    [MaxLength(32)]
    public string Level { get; set; } = "Info";

    [MaxLength(1024)]
    public string Message { get; set; } = string.Empty;

    [ForeignKey(nameof(RipJobId))]
    public RipJob? RipJob { get; set; }
}
