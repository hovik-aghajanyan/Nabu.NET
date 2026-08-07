using System;
using System.Collections.Generic;
using System.Linq;
using Nabu.Sample.TodoApi.Models;

namespace Nabu.Sample.TodoApi.Services
{
    /// <summary>Storage for todo items. Scoped per owner so users never see each other's data.</summary>
    public interface ITodoRepository
    {
        IReadOnlyList<TodoItem> List(string owner, bool? isCompleted, TodoPriority? priority, string? search);

        TodoItem? Find(string owner, Guid id);

        TodoItem Add(TodoItem item);

        TodoItem? Update(string owner, Guid id, Action<TodoItem> mutate);

        bool Delete(string owner, Guid id);
    }

    /// <summary>In-memory repository so the sample runs with no external dependencies.</summary>
    public sealed class InMemoryTodoRepository : ITodoRepository
    {
        private readonly object _sync = new object();
        private readonly List<TodoItem> _items = new List<TodoItem>();

        public InMemoryTodoRepository()
        {
            Add(new TodoItem
            {
                Title = "Write the design document",
                Notes = "Cover the pipeline replay approach.",
                Priority = TodoPriority.High,
                Owner = "alice",
                Tags = new List<string> { "docs" },
                DueOn = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            });

            Add(new TodoItem
            {
                Title = "Review the pull request",
                Priority = TodoPriority.Normal,
                Owner = "alice",
                Tags = new List<string> { "code-review" },
            });

            Add(new TodoItem
            {
                Title = "Rotate the signing keys",
                Priority = TodoPriority.Critical,
                Owner = "root",
                Tags = new List<string> { "ops", "security" },
            });
        }

        public IReadOnlyList<TodoItem> List(string owner, bool? isCompleted, TodoPriority? priority, string? search)
        {
            lock (_sync)
            {
                IEnumerable<TodoItem> query = _items.Where(i => i.Owner == owner);

                if (isCompleted.HasValue)
                {
                    query = query.Where(i => i.IsCompleted == isCompleted.Value);
                }

                if (priority.HasValue)
                {
                    query = query.Where(i => i.Priority == priority.Value);
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    query = query.Where(i =>
                        i.Title.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (i.Notes != null && i.Notes.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
                }

                return query.OrderByDescending(i => i.Priority).ThenBy(i => i.CreatedAt).ToList();
            }
        }

        public TodoItem? Find(string owner, Guid id)
        {
            lock (_sync)
            {
                return _items.FirstOrDefault(i => i.Id == id && i.Owner == owner);
            }
        }

        public TodoItem Add(TodoItem item)
        {
            lock (_sync)
            {
                if (item.Id == Guid.Empty)
                {
                    item.Id = Guid.NewGuid();
                }

                if (item.CreatedAt == default)
                {
                    item.CreatedAt = DateTimeOffset.UtcNow;
                }

                _items.Add(item);
                return item;
            }
        }

        public TodoItem? Update(string owner, Guid id, Action<TodoItem> mutate)
        {
            lock (_sync)
            {
                var item = _items.FirstOrDefault(i => i.Id == id && i.Owner == owner);
                if (item == null)
                {
                    return null;
                }

                mutate(item);
                return item;
            }
        }

        public bool Delete(string owner, Guid id)
        {
            lock (_sync)
            {
                var item = _items.FirstOrDefault(i => i.Id == id && i.Owner == owner);
                return item != null && _items.Remove(item);
            }
        }
    }
}
