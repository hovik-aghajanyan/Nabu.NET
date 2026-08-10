namespace Nabu.Sample.OfficialSdk.Models
{
    /// <summary>A catalog entry.</summary>
    public class Book
    {
        public int Id { get; set; }

        /// <summary>Title of the book.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Author of the book.</summary>
        public string Author { get; set; } = string.Empty;

        /// <summary>Year of first publication.</summary>
        public int Year { get; set; }
    }
}
