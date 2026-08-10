using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.OfficialSdk.Models;
using Nabu.Sample.OfficialSdk.Services;

namespace Nabu.Sample.OfficialSdk.Controllers
{
    /// <summary>A small book catalog exposed both as a REST API and as MCP tools.</summary>
    [ApiController]
    [Route("api/books")]
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
        [McpTool("books_search", Description = "Search the book catalog by title or author.", ReadOnly = true)]
        public ActionResult<IReadOnlyList<Book>> Search([FromQuery] string? query)
        {
            return Ok(_catalog.Search(query));
        }

        /// <summary>Fetches one book by its id.</summary>
        /// <param name="id">Identifier returned by a search.</param>
        [HttpGet("{id:int}")]
        [McpTool("books_get", Description = "Fetch a single book by id.", ReadOnly = true)]
        public ActionResult<Book> Get(int id)
        {
            var book = _catalog.Get(id);
            return book == null ? NotFound() : Ok(book);
        }

        /// <summary>Adds a book to the catalog.</summary>
        /// <param name="request">The book to add.</param>
        [HttpPost]
        [McpTool("books_add", Description = "Add a book to the catalog.")]
        public ActionResult<Book> Add([FromBody] AddBookRequest request)
        {
            var book = _catalog.Add(request.Title, request.Author, request.Year);
            return CreatedAtAction(nameof(Get), new { id = book.Id }, book);
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
