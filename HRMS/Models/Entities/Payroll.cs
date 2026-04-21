using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Entities
{
    public class Payroll
    {
        public int Id { get; set; }

        public int EmployeeId { get; set; }
        public Employee Employee { get; set; }

        public int Month { get; set; } // 1-12
        public int Year { get; set; }

        public decimal BasicSalary { get; set; }
        public decimal Bonus { get; set; }
        public decimal Deduction { get; set; }
        public decimal Tax { get; set; }
        public decimal NetSalary { get; set; }

        public DateTime GeneratedDate { get; set; } = DateTime.Now;
        public bool IsPaid { get; set; } = false;
        public DateTime? PaidDate { get; set; }

        public int GeneratedBy { get; set; }
        public User GeneratedByUser { get; set; }
    }
}
