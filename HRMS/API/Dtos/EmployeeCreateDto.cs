using System.ComponentModel.DataAnnotations;

namespace API.Dtos
{
    // 1. Create this DTO in HRMS.API/Dtos/
    public class EmployeeCreateDto
    {
        [Required]
        [StringLength(150)]
        public string Name { get; set; } = null!;

        [Required]
        [EmailAddress]
        public string Email { get; set; } = null!;

        [Phone]
        public string? Phone { get; set; }

        [Required]
        public int DepartmentId { get; set; }

        [StringLength(50)]
        public string? AccountNumber { get; set; }
        public string? Password { get; set; }
    }
}
