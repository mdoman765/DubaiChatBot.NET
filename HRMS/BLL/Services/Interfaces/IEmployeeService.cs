using Models.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BLL.Services.Interfaces
{
    public interface IEmployeeService
    {
        /// <summary>
        /// Gets all active employees (respects IsActive soft-delete flag)
        /// </summary>
        Task<List<Employee>> GetAllAsync();

        /// <summary>
        /// Gets a single employee by ID (returns null if not found or inactive)
        /// </summary>
        Task<Employee?> GetByIdAsync(int id);

        /// <summary>
        /// Creates a new employee with audit fields set
        /// </summary>
        /// <param name="employee">Employee entity to create</param>
        /// <param name="createdByUserId">ID of the currently logged-in user (for AddedBy)</param>
        /// <returns>The created employee with assigned ID</returns>
        Task<Employee> CreateAsync(Employee employee, int createdByUserId);
        Task<User> CreateUserAsync(User user, int currentUserId);
        /// <summary>
        /// Updates an existing employee (only allowed fields, preserves audit trail)
        /// </summary>
        /// <returns>true if updated successfully, false if not found</returns>
        Task<bool> UpdateAsync(Employee employee, int updatedByUserId);

        /// <summary>
        /// Soft-deletes an employee by setting IsActive = false
        /// </summary>
        /// <returns>true if soft-deleted, false if not found</returns>
        Task<bool> SoftDeleteAsync(int id, int deletedByUserId);

        // Optional: you can add more later
        // Task<List<Employee>> GetByDepartmentAsync(int departmentId);
        // Task<bool> ExistsAsync(int id);
    }
}
