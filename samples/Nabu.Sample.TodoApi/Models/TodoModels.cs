using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Nabu.Sample.TodoApi.Models
{
    /// <summary>How urgent a todo item is.</summary>
    public enum TodoPriority
    {
        Low,
        Normal,
        High,
        Critical,
    }

    /// <summary>A single todo item belonging to one user.</summary>
    public class TodoItem
    {
        /// <summary>Stable identifier of the item.</summary>
        public Guid Id { get; set; }

        /// <summary>Short summary of what needs to be done.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Optional longer description.</summary>
        public string? Notes { get; set; }

        /// <summary>Whether the item has been completed.</summary>
        public bool IsCompleted { get; set; }

        /// <summary>How urgent the item is.</summary>
        public TodoPriority Priority { get; set; }

        /// <summary>Optional due date, in UTC.</summary>
        public DateTimeOffset? DueOn { get; set; }

        /// <summary>Free-form labels used for grouping.</summary>
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>Login of the user the item belongs to.</summary>
        public string Owner { get; set; } = string.Empty;

        /// <summary>When the item was created, in UTC.</summary>
        public DateTimeOffset CreatedAt { get; set; }
    }

    /// <summary>Payload for creating a todo item.</summary>
    public class CreateTodoRequest
    {
        /// <summary>Short summary of what needs to be done.</summary>
        [Required]
        [StringLength(120, MinimumLength = 1)]
        public string Title { get; set; } = string.Empty;

        /// <summary>Optional longer description.</summary>
        [StringLength(2000)]
        public string? Notes { get; set; }

        /// <summary>How urgent the item is.</summary>
        [DefaultValue(TodoPriority.Normal)]
        public TodoPriority Priority { get; set; } = TodoPriority.Normal;

        /// <summary>Optional due date, in UTC.</summary>
        public DateTimeOffset? DueOn { get; set; }

        /// <summary>Free-form labels used for grouping.</summary>
        public List<string>? Tags { get; set; }
    }

    /// <summary>Payload for updating an existing todo item. Omitted fields are left unchanged.</summary>
    public class UpdateTodoRequest
    {
        /// <summary>New title.</summary>
        [StringLength(120, MinimumLength = 1)]
        public string? Title { get; set; }

        /// <summary>New notes.</summary>
        [StringLength(2000)]
        public string? Notes { get; set; }

        /// <summary>New completion state.</summary>
        public bool? IsCompleted { get; set; }

        /// <summary>New priority.</summary>
        public TodoPriority? Priority { get; set; }

        /// <summary>New due date, in UTC.</summary>
        public DateTimeOffset? DueOn { get; set; }
    }

    /// <summary>A page of todo items.</summary>
    public class TodoPage
    {
        /// <summary>Items on this page.</summary>
        public List<TodoItem> Items { get; set; } = new List<TodoItem>();

        /// <summary>Total number of items matching the filter, across all pages.</summary>
        public int TotalCount { get; set; }

        /// <summary>Zero-based index of this page.</summary>
        public int Page { get; set; }

        /// <summary>Number of items requested per page.</summary>
        public int PageSize { get; set; }
    }

    /// <summary>Credentials exchanged for an access token.</summary>
    public class LoginRequest
    {
        /// <summary>User login. The sample accepts "alice" (user) and "root" (administrator).</summary>
        [Required]
        public string Username { get; set; } = string.Empty;

        /// <summary>Password. Every sample account uses "password".</summary>
        [Required]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>An issued access token.</summary>
    public class LoginResponse
    {
        public string AccessToken { get; set; } = string.Empty;

        public string TokenType { get; set; } = "Bearer";

        public int ExpiresInSeconds { get; set; }
    }
}
