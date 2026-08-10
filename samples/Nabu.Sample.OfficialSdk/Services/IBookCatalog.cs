using System.Collections.Generic;
using Nabu.Sample.OfficialSdk.Models;

namespace Nabu.Sample.OfficialSdk.Services
{
    public interface IBookCatalog
    {
        IReadOnlyList<Book> Search(string? query);

        Book? Get(int id);

        Book Add(string title, string author, int year);
    }
}
