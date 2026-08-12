using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.TodoApi.Models;
using Nabu.Sample.TodoApi.Services;

namespace Nabu.Sample.TodoApi.Controllers
{
    /// <summary>
    /// The todo API. Nothing here is MCP-specific beyond the <c>[McpTool]</c> attributes: the actions
    /// are ordinary MVC actions, and the <c>[Authorize]</c> attributes below are enforced for MCP tool
    /// calls exactly as they are for direct HTTP calls.
    /// </summary>
    [ApiController]
    [Route("api/todos")]
    [Authorize]
    [Produces("application/json")]
    public class TodosController : ControllerBase
    {
        private readonly ITodoRepository _repository;

        public TodosController(ITodoRepository repository)
        {
            _repository = repository;
        }

        private string Owner => User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "anonymous";

        /// <summary>Lists the priority levels a todo item can be given.</summary>
        /// <remarks>
        /// Reference data, of no use to anyone as a secret, so the action opts out of the controller's
        /// <see cref="AuthorizeAttribute"/> with a plain <see cref="AllowAnonymousAttribute"/>. That is
        /// all it takes: <c>[AllowAnonymous]</c> wins over the controller's <c>[Authorize]</c> for a tool
        /// call exactly as it does for an HTTP request, so this is the one todo tool an unauthenticated
        /// caller is shown - and the only one it can call.
        /// </remarks>
        [HttpGet("priorities")]
        [AllowAnonymous]
        [McpTool]
        public ActionResult<IEnumerable<string>> GetPriorities()
        {
            return Ok(Enum.GetNames(typeof(TodoPriority)));
        }

        /// <summary>Lists the todo items belonging to the signed-in user.</summary>
        /// <param name="isCompleted">Filters by completion state. Omit to return both.</param>
        /// <param name="priority">Filters by priority.</param>
        /// <param name="search">Case-insensitive substring matched against the title and notes.</param>
        /// <param name="page">Zero-based page index.</param>
        /// <param name="pageSize">Number of items per page, between 1 and 100.</param>
        [HttpGet]
        [McpTool]
        [McpTool(
            "todos_list_open",
            Title = "List open todos",
            Description = "Lists the todo items that are still open. Completed items are never returned.",
            ExcludeParameters = new[] { "search", "priority" },
            ConstantParameters = new[] { "isCompleted=false" })]
        [McpTool(
            "todos_search",
            Title = "Search todos",
            Description = "Searches the signed-in user's todo items by title and notes.",
            IncludeParameters = new[] { "search", "page", "pageSize" },
            RequiredParameters = new[] { "search" })]
        public ActionResult<TodoPage> List(
            [FromQuery] bool? isCompleted,
            [FromQuery] TodoPriority? priority,
            [FromQuery] string? search,
            [FromQuery] int page = 0,
            [FromQuery] [System.ComponentModel.DataAnnotations.Range(1, 100)] int pageSize = 20)
        {
            var all = _repository.List(Owner, isCompleted, priority, search);
            var items = all.Skip(page * pageSize).Take(pageSize).ToList();

            return Ok(new TodoPage
            {
                Items = items,
                TotalCount = all.Count,
                Page = page,
                PageSize = pageSize,
            });
        }

        /// <summary>Fetches a single todo item by its identifier.</summary>
        /// <param name="id">Identifier of the item.</param>
        [HttpGet("{id:guid}")]
        [McpTool]
        public ActionResult<TodoItem> GetById(Guid id)
        {
            var item = _repository.Find(Owner, id);
            return item == null ? NotFound(new { message = "No todo item with id " + id + "." }) : (ActionResult<TodoItem>)Ok(item);
        }

        /// <summary>Creates a todo item for the signed-in user.</summary>
        /// <param name="request">The item to create.</param>
        [HttpPost]
        [McpTool(Idempotent = false)]
        public ActionResult<TodoItem> Create([FromBody] CreateTodoRequest request)
        {
            var item = _repository.Add(new TodoItem
            {
                Title = request.Title,
                Notes = request.Notes,
                Priority = request.Priority,
                DueOn = request.DueOn,
                Tags = request.Tags ?? new List<string>(),
                Owner = Owner,
            });

            return CreatedAtAction(nameof(GetById), new { id = item.Id }, item);
        }

        /// <summary>Updates the fields of an existing todo item that are present in the request.</summary>
        /// <param name="id">Identifier of the item to update.</param>
        /// <param name="request">The fields to change.</param>
        [HttpPatch("{id:guid}")]
        [McpTool(Destructive = false)]
        public ActionResult<TodoItem> Update(Guid id, [FromBody] UpdateTodoRequest request)
        {
            var item = _repository.Update(Owner, id, existing =>
            {
                if (request.Title != null)
                {
                    existing.Title = request.Title;
                }

                if (request.Notes != null)
                {
                    existing.Notes = request.Notes;
                }

                if (request.IsCompleted.HasValue)
                {
                    existing.IsCompleted = request.IsCompleted.Value;
                }

                if (request.Priority.HasValue)
                {
                    existing.Priority = request.Priority.Value;
                }

                if (request.DueOn.HasValue)
                {
                    existing.DueOn = request.DueOn;
                }
            });

            return item == null ? NotFound(new { message = "No todo item with id " + id + "." }) : (ActionResult<TodoItem>)Ok(item);
        }

        /// <summary>Attaches a file to a todo item. The sample keeps the file's metadata, not its bytes.</summary>
        /// <remarks>
        /// A form-binding action publishes like any other: the <paramref name="file"/> argument is
        /// advertised to the model as base64 content plus an optional file name and MIME type, and the
        /// tool call is replayed as a real multipart/form-data request - so this action reads its
        /// <see cref="IFormFile"/> exactly as it would for a browser upload.
        /// </remarks>
        /// <param name="id">Identifier of the item to attach the file to.</param>
        /// <param name="file">The file to attach.</param>
        /// <param name="description">Optional description of the attachment.</param>
        [HttpPost("{id:guid}/attachments")]
        [McpTool(Idempotent = false)]
        public ActionResult<TodoAttachment> Attach(
            Guid id,
            IFormFile file,
            [FromForm] [System.ComponentModel.DataAnnotations.StringLength(500)] string? description)
        {
            var attachment = new TodoAttachment
            {
                Id = Guid.NewGuid(),
                FileName = file.FileName,
                ContentType = file.ContentType,
                SizeBytes = file.Length,
                Description = description,
            };

            var item = _repository.Update(Owner, id, existing => existing.Attachments.Add(attachment));
            return item == null
                ? NotFound(new { message = "No todo item with id " + id + "." })
                : (ActionResult<TodoAttachment>)Ok(attachment);
        }

        /// <summary>
        /// Permanently deletes a todo item. Restricted to administrators, which the MCP endpoint honours
        /// because the tool call runs through the same authorization pipeline.
        /// </summary>
        /// <param name="id">Identifier of the item to delete.</param>
        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "AdminOnly")]
        [McpTool]
        public IActionResult Delete(Guid id)
        {
            return _repository.Delete(Owner, id)
                ? NoContent()
                : NotFound(new { message = "No todo item with id " + id + "." });
        }

        /// <summary>
        /// Internal maintenance endpoint. It stays reachable over HTTP but is deliberately hidden from
        /// MCP clients.
        /// </summary>
        [HttpPost("reindex")]
        [McpIgnore]
        public IActionResult Reindex() => Ok(new { reindexed = true });
    }
}
