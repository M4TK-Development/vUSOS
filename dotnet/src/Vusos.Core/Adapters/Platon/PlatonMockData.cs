namespace Vusos.Core.Adapters.Platon;

// Definicje zbioru demo Platona (deterministyczne; wszystkie daty liczone względem zegara w PlatonMockAdapter).

internal sealed record MockFile(string Name, string Mime, long Size);

internal sealed record MockLink(string Name, string Url);

internal sealed record MockCourse(
    int Id, string Short, string Full, string Teacher, double? Progress, string Color, int Weeks, int LastAccessHours,
    IReadOnlyList<MockFile> Material, MockLink Link);

internal sealed record MockGrade(double Value, double Max);

internal sealed record MockAssignment(
    string Key, int Course, string Name, int DueDays, bool Submitted, MockGrade? Grade, string Description);

internal enum MockQuestionKind { MultiChoice, TrueFalse, ShortAnswer }

/// <summary>Pytanie quizu. <see cref="Correct"/>: indeks opcji (multichoice), "1"/"0" (prawda/fałsz) albo dopuszczalne odpowiedzi tekstowe.</summary>
internal sealed record MockQuestion(
    int Slot, int Page, MockQuestionKind Kind, string Text, double MaxMark,
    IReadOnlyList<string> Choices, IReadOnlyList<string> Correct)
{
    public string TypeName => Kind switch
    {
        MockQuestionKind.MultiChoice => "multichoice",
        MockQuestionKind.TrueFalse => "truefalse",
        _ => "shortanswer"
    };
}

internal sealed record MockQuiz(
    string Key, int Course, string Name, int CloseDays, bool AllowOffline, int? TimeLimitSeconds, int Attempts,
    double Grade, string Intro, IReadOnlyList<MockQuestion> Questions)
{
    public int PageCount => Questions.Max(q => q.Page) + 1;
}

internal static class MockData
{
    private const string Pdf = "application/pdf";
    private const string Pptx = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    private const string Sql = "application/sql";

    public const string PptxMime = Pptx;
    public const string PdfMime = Pdf;

    public static readonly IReadOnlyList<MockCourse> Courses =
    [
        new(3101, "INF-ALG-2025Z", "Algorytmy i struktury danych", "dr Anna Nowak", 43, "#2563eb", 2, 5,
            [new("Cormen_rozdzial_2.pdf", Pdf, 1_204_331)],
            new("Visualgo - wizualizacje algorytmów", "https://visualgo.net/en")),
        new(3102, "INF-BD-2025Z", "Bazy danych", "dr inż. Piotr Zieliński", 31, "#0d9488", 2, 26,
            [new("Model_relacyjny.pdf", Pdf, 845_120), new("Skrypt_SQL_laboratorium.sql", Sql, 6_410)],
            new("PostgreSQL - dokumentacja", "https://www.postgresql.org/docs/")),
        new(3103, "INF-PROG-2025Z", "Programowanie obiektowe", "mgr Katarzyna Wójcik", 55, "#7c3aed", 1, 3,
            [new("Wzorce_projektowe.pdf", Pdf, 2_310_998)],
            new("Refactoring Guru - wzorce", "https://refactoring.guru/pl/design-patterns")),
        new(3104, "INF-SO-2025Z", "Systemy operacyjne", "dr hab. Marek Lewandowski", 20, "#dc2626", 1, 72,
            [new("Procesy_i_watki.pdf", Pdf, 1_540_776)],
            new("The Linux man-pages", "https://man7.org/linux/man-pages/")),
        new(3105, "MAT-DYS-2025Z", "Matematyka dyskretna", "prof. Ewa Kamińska", 68, "#d97706", 0, 120,
            [new("Logika_zdan.pdf", Pdf, 612_004)],
            new("Zbiór zadań - kombinatoryka", "https://example.org/kombinatoryka")),
        new(3106, "ANG-B2-2025Z", "Język angielski B2", "mgr Sarah Mitchell", null, "#059669", 0, 48,
            [new("Unit_4_vocabulary.pdf", Pdf, 402_118)],
            new("Cambridge Dictionary", "https://dictionary.cambridge.org/"))
    ];

    public static readonly IReadOnlyList<MockAssignment> Assignments =
    [
        new("alg-1", 0, "Zadanie 1: Sortowanie", 3, false, null, "Zaimplementuj quicksort i mergesort oraz porównaj czasy działania."),
        new("alg-0", 0, "Zadanie 0: Rozgrzewka", -8, true, new(9, 10), "Proste zadanie na złożoność obliczeniową."),
        new("bd-erd", 1, "Projekt schematu ERD", 6, false, null, "Zaprojektuj schemat ERD dla systemu rezerwacji sal."),
        new("bd-lab3", 1, "Laboratorium 3: SQL JOIN", -2, false, null, "Zestaw zapytań z użyciem JOIN i GROUP BY."),
        new("prog-2", 2, "Zadanie 2: Dziedziczenie", 1, true, null, "Hierarchia klas pojazdów z polimorfizmem."),
        new("prog-proj", 2, "Projekt: system biblioteczny", 20, false, null, "Projekt zaliczeniowy w parach."),
        new("so-lab2", 3, "Sprawozdanie z lab 2: procesy", 5, false, null, "Sprawozdanie w PDF, max 3 strony."),
        new("mat-3", 4, "Zestaw 3: indukcja", 12, false, null, "Dowody indukcyjne."),
        new("ang-essay", 5, "Essay: My study plans", 4, false, null, "Write 250 words about your study plans.")
    ];

    public static readonly IReadOnlyList<MockQuiz> Quizzes =
    [
        // Quiz z podejściami offline: 3 strony, 6 pytań (multichoice, prawda/fałsz, odpowiedź krótka).
        new("alg-q1", 0, "Quiz: złożoność obliczeniowa", 2, AllowOffline: true, TimeLimitSeconds: 1200, Attempts: 2, Grade: 10,
            Intro: "<p>Sześć pytań o złożoność obliczeniową. Czas: 20 minut. Dwa podejścia.</p>",
            Questions:
            [
                new(1, 0, MockQuestionKind.MultiChoice, "Jaka jest złożoność czasowa wyszukiwania binarnego w posortowanej tablicy n elementów?", 1,
                    ["O(n)", "O(log n)", "O(n log n)", "O(1)"], ["1"]),
                new(2, 0, MockQuestionKind.TrueFalse, "Quicksort w najgorszym przypadku działa w czasie O(n log n).", 1,
                    [], ["0"]),
                new(3, 1, MockQuestionKind.MultiChoice, "Która notacja opisuje dolne ograniczenie asymptotyczne funkcji?", 1,
                    ["O", "Ω", "Θ", "o"], ["1"]),
                new(4, 1, MockQuestionKind.ShortAnswer, "Podaj nazwę algorytmu sortowania, który dzieli tablicę względem elementu osiowego (pivota).", 2,
                    [], ["quicksort", "sortowanie szybkie"]),
                new(5, 2, MockQuestionKind.TrueFalse, "Mergesort jest algorytmem stabilnym.", 1,
                    [], ["1"]),
                new(6, 2, MockQuestionKind.ShortAnswer, "Ile wynosi log₂(1024)? Podaj liczbę.", 1,
                    [], ["10"])
            ]),
        // Quiz tylko online (bez podejść offline): 2 strony, 3 pytania.
        new("mat-q1", 4, "Test z logiki zdań", 9, AllowOffline: false, TimeLimitSeconds: 1800, Attempts: 1, Grade: 10,
            Intro: "<p>Test z rachunku zdań. Jedno podejście, 30 minut.</p>",
            Questions:
            [
                new(1, 0, MockQuestionKind.MultiChoice, "Implikacja p → q jest fałszywa wtedy i tylko wtedy, gdy:", 1,
                    ["p jest fałszywe, a q prawdziwe", "p jest prawdziwe, a q fałszywe", "p i q są fałszywe", "p i q są prawdziwe"], ["1"]),
                new(2, 0, MockQuestionKind.TrueFalse, "Zdanie p ∨ ¬p jest tautologią.", 1,
                    [], ["1"]),
                new(3, 1, MockQuestionKind.ShortAnswer, "Ile wierszy ma tablica prawdy dla trzech zmiennych zdaniowych?", 1,
                    [], ["8", "osiem"])
            ])
    ];
}
