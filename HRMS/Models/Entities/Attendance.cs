using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Models.Entities
{
    public class Attendance
    {
        public int Id { get; set; }

        public int EmployeeId { get; set; }
        public Employee Employee { get; set; }

        public DateTime CheckInTime { get; set; }
        public DateTime? CheckOutTime { get; set; }

        public string Status { get; set; } // Present / Absent / Late / Leave

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        public int? CreatedBy { get; set; }
        public User CreatedByUser { get; set; }

        public int? UpdatedBy { get; set; }
        public User UpdatedByUser { get; set; }

        public bool IsDeleted { get; set; } = false;
    }
}
