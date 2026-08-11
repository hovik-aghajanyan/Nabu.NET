using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.OfficialSdk.Models;
using Nabu.Sample.OfficialSdk.Services;

namespace Nabu.Sample.OfficialSdk.Controllers
{
    /// <summary>
    /// A small book catalog exposed both as a REST API and as MCP tools. The controller requires a
    /// signed-in caller; the read-only actions opt out with <see cref="AllowAnonymousAttribute"/>, so
    /// an anonymous MCP client is shown (and can call) just the search tools, a signed-in user can
    /// also add books, and only an administrator sees <c>books_remove</c>. The authorization rules
    /// are enforced for tool calls exactly as for direct HTTP requests - the official SDK protocol
    /// layer changes nothing about that.
    /// </summary>
    [ApiController]
    [Route("api/books")]
    [Authorize]
    public class BooksController : ControllerBase
    {
        private readonly IBookCatalog _catalog;

        public BooksController(IBookCatalog catalog)
        {
            _catalog = catalog;
        }

        /// <summary>Searches the catalog by title or author.</summary>
        /// <param name="query">Text to look for; omit it to list every book.</param>
        [HttpGet]
        [AllowAnonymous]
        [McpTool("books_search", Description = "Search the book catalog by title or author.", ReadOnly = true)]
        public ActionResult<IReadOnlyList<Book>> Search([FromQuery] string? query)
        {
            return Ok(_catalog.Search(query));
        }

        /// <summary>Fetches one book by its id.</summary>
        /// <param name="id">Identifier returned by a search.</param>
        [HttpGet("{id:int}")]
        [AllowAnonymous]
        [McpTool("books_get", Description = "Fetch a single book by id.", ReadOnly = true)]
        public ActionResult<Book> Get(int id)
        {
            var book = _catalog.Get(id);
            return book == null ? NotFound() : Ok(book);
        }

        /// <summary>Adds a book to the catalog. Requires a signed-in caller.</summary>
        /// <param name="request">The book to add.</param>
        [HttpPost]
        [McpTool("books_add", Description = "Add a book to the catalog.")]
        public ActionResult<Book> Add([FromBody] AddBookRequest request)
        {
            var book = _catalog.Add(request.Title, request.Author, request.Year);
            return CreatedAtAction(nameof(Get), new { id = book.Id }, book);
        }

        /// <summary>
        /// Permanently removes a book from the catalog. Restricted to administrators, which the MCP
        /// endpoint honours because the tool call runs through the same authorization pipeline.
        /// </summary>
        /// <param name="id">Identifier of the book to remove.</param>
        [HttpDelete("{id:int}")]
        [Authorize(Policy = "AdminOnly")]
        [McpTool("books_remove", Description = "Remove a book from the catalog. Administrators only.")]
        public IActionResult Remove(int id)
        {
            return _catalog.Remove(id) ? NoContent() : NotFound();
        }
    }

    /// <summary>Details of the book to add.</summary>
    public class AddBookRequest
    {
        /// <summary>Title of the book.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Author of the book.</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>Year of first publication.</summary>
        public int Year { get; set; }
    }
}
