using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Entities
{
    public class Employee
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }

        public int DepartmentId { get; set; }
        public Department Department { get; set; }

        public int? DesignationId { get; set; } // Optional, create Designation entity if needed

        public string AccountNumber { get; set; }

        public DateTime AddedDate { get; set; } = DateTime.Now;
        public int? AddedBy { get; set; }
        public User AddedByUser { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime? UpdatedDate { get; set; }

        // Navigation
        public ICollection<Salary> Salaries { get; set; }
        public ICollection<Payroll> Payrolls { get; set; }
        public ICollection<Attendance> Attendances { get; set; }
    }
}
