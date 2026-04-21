using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Entities
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }

        public int RoleId { get; set; }
        public Role Role { get; set; }

        public DateTime AddedDate { get; set; } = DateTime.Now;
        public DateTime? UpdatedDate { get; set; }
        public int? AddedBy { get; set; }
        public User AddedByUser { get; set; }

        public DateTime? LastLoginDate { get; set; }
        public bool IsActive { get; set; } = true;

        // Navigation
        public ICollection<Employee> EmployeesAdded { get; set; }
        public ICollection<Salary> SalariesCreated { get; set; }
        public ICollection<Payroll> PayrollsGenerated { get; set; }
        public ICollection<Attendance> AttendanceCreated { get; set; }
        public ICollection<Attendance> AttendanceUpdated { get; set; }
    }
}
