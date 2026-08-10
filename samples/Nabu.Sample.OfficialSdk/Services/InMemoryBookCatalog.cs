using System;
using System.Collections.Generic;
using System.Linq;
using Nabu.Sample.OfficialSdk.Models;

namespace Nabu.Sample.OfficialSdk.Services
{
    public class InMemoryBookCatalog : IBookCatalog
    {
        private readonly object _gate = new();
        private readonly List<Book> _books = new()
        {
            new Book { Id = 1, Title = "The Epic of Gilgamesh", Author = "Sîn-lēqi-unninni", Year = -1200 },
            new Book { Id = 2, Title = "Enūma Eliš", Author = "Unknown", Year = -1100 },
            new Book { Id = 3, Title = "A History of Babylon", Author = "Paul-Alain Beaulieu", Year = 2018 },
        };

        public IReadOnlyList<Book> Search(string? query)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return _books.ToList();
                }

                return _books
                    .Where(book =>
                        book.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        book.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        public Book? Get(int id)
        {
            lock (_gate)
            {
                return _books.FirstOrDefault(book => book.Id == id);
            }
        }

        public Book Add(string title, string author, int year)
        {
            lock (_gate)
            {
                var book = new Book
                {
                    Id = _books.Count == 0 ? 1 : _books.Max(b => b.Id) + 1,
                    Title = title,
                    Author = author,
                    Year = year,
                };

                _books.Add(book);
                return book;
            }
        }
    }
}
