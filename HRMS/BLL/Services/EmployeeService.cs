using BLL.Services.Interfaces;
using BLL.Services.Interfaces;
using DAL.Repositories;
using DAL.Repositories;           // your repository namespace
using Microsoft.EntityFrameworkCore;
using Models.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BLL.Services
{
    public class EmployeeService : IEmployeeService
    {
        private readonly IEmployeeRepository _employeeRepository;

        public EmployeeService(IEmployeeRepository employeeRepository)
        {
            _employeeRepository = employeeRepository ?? throw new ArgumentNullException(nameof(employeeRepository));
        }

        public async Task<List<Employee>> GetAllAsync()
        {
            return await _employeeRepository.GetAllAsync();
        }

        public async Task<Employee?> GetByIdAsync(int id)
        {
            if (id <= 0)
                return null;

            return await _employeeRepository.GetByIdAsync(id);
        }

        public async Task<Employee> CreateAsync(Employee employee, int createdByUserId)
        {
            if (employee == null)
                throw new ArgumentNullException(nameof(employee));

            // Basic business validation
            if (string.IsNullOrWhiteSpace(employee.Name))
                throw new ArgumentException("Employee name is required.");

            if (string.IsNullOrWhiteSpace(employee.Email))
                throw new ArgumentException("Email is required.");

            if (employee.DepartmentId <= 0)
                throw new ArgumentException("Valid department is required.");

            // Set audit fields
            employee.AddedDate = DateTime.UtcNow;
            employee.AddedBy = createdByUserId;
            employee.IsActive = true;
            employee.UpdatedDate = null;

            // You can add more checks here (email uniqueness, etc.)
            // if (await _employeeRepository.EmailExistsAsync(employee.Email))
            //     throw new InvalidOperationException("Email already in use.");

            await _employeeRepository.AddAsync(employee);

            // Assuming repository saves changes and sets the ID
            return employee;
        }
        public async Task<User> CreateUserAsync(User user, int createdByUserId)
        {
            if (user == null)
                throw new ArgumentNullException(nameof(user));

            if (string.IsNullOrWhiteSpace(user.Username))
                throw new ArgumentException("Username is required.");

            if (string.IsNullOrWhiteSpace(user.PasswordHash))
                throw new ArgumentException("Password is required.");

            user.AddedDate = DateTime.UtcNow;
            user.AddedBy = createdByUserId;
            user.IsActive = true;
            user.UpdatedDate = null;

            await _employeeRepository.AddUserAsync(user);

            return user;
        }
        //public async Task<User> CreateUserAsync(User user, int currentUserId)
        //{
        //    if (user == null) throw new ArgumentNullException(nameof(user));

        //    user.AddedBy = currentUserId;
        //    user.AddedDate = DateTime.UtcNow;

        //    await _employeeRepository.AddUserAsync(user);
        //    await _employeeRepository.AddUserAsync.SaveChangesAsync(); // This generates user.Id

        //    return user; // now has Id populated
        //}
        public async Task<bool> UpdateAsync(Employee employee, int updatedByUserId)
        {
            if (employee == null || employee.Id <= 0)
                return false;

            var existing = await _employeeRepository.GetByIdAsync(employee.Id);
            if (existing == null || !existing.IsActive)
                return false;

            // Only update allowed fields (protect audit fields, ID, etc.)
            existing.Name = employee.Name?.Trim() ?? existing.Name;
            existing.Email = employee.Email?.Trim() ?? existing.Email;
            existing.Phone = employee.Phone?.Trim();
            existing.DepartmentId = employee.DepartmentId > 0 ? employee.DepartmentId : existing.DepartmentId;
            existing.AccountNumber = employee.AccountNumber?.Trim();
            existing.UpdatedDate = DateTime.UtcNow;
            // existing.AddedBy remains unchanged
            // existing.AddedDate remains unchanged

            await _employeeRepository.UpdateAsync(existing);

            return true;
        }

        public async Task<bool> SoftDeleteAsync(int id, int deletedByUserId)
        {
            if (id <= 0)
                return false;

            var employee = await _employeeRepository.GetByIdAsync(id);
            if (employee == null || !employee.IsActive)
                return false;

            employee.IsActive = false;
            employee.UpdatedDate = DateTime.UtcNow;
            // You could add DeletedBy if you ever create that field

            await _employeeRepository.UpdateAsync(employee);

            return true;
        }
    }
}