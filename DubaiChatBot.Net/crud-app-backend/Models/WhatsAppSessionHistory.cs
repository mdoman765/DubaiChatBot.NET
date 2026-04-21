using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace crud_app_backend.Models
{
    public class WhatsAppSessionHistory
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("Id")]
        public long Id { get; set; }

        [Required]
        [MaxLength(30)]
        [Column("Phone")]
        public string Phone { get; set; } = default!;

        [Required]
        [MaxLength(50)]
        [Column("FromStep")]
        public string FromStep { get; set; } = default!;

        [Required]
        [MaxLength(50)]
        [Column("ToStep")]
        public string ToStep { get; set; } = default!;

        [MaxLength(1000)]
        [Column("RawMessage")]
        public string? RawMessage { get; set; }

        [Column("TempDataSnapshot", TypeName = "nvarchar(max)")]
        public string? TempDataSnapshot { get; set; }

        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Foreign key
        [ForeignKey(nameof(Phone))]
        public WhatsAppSession Session { get; set; } = default!;
    }
}
