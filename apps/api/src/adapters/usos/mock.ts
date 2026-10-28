/**
 * USOS adapter, mode "mock": deterministic, realistic data for a fictional Vistula student
 * (Informatyka, studia I stopnia, semestr zimowy 2025/26). All ids are stable.
 *
 * - Terms, courses and grades are fixed (2025Z is always "current", independent of the system clock).
 * - Timetable is generated for whatever [from, to] range is requested: every Mon-Fri has classes.
 * - Payments, messages and announcements are dated RELATIVE to `now` (injectable) so the demo never looks stale;
 *   the ids stay the same.
 * All names, e-mail addresses (.example) and phone numbers are fictional.
 */
import type {
  Announcement,
  Contact,
  CourseUnit,
  Grade,
  LangText,
  Message,
  MessageThread,
  Payment,
  StudentProfile,
  Term,
  TimetableEntry,
  UsosAdapter
} from "@vusos/shared";
import { AdapterUnavailableError } from "@vusos/shared";
import type { UsosAdapterDeps } from "./config.js";
import { describeGrade } from "./mappers.js";
import { addDays, eachDay, toDateOnly, warsawDateOf, warsawLocalToIso, weekday } from "./time.js";

export const MOCK_CURRENT_TERM = "2025Z";
const STUDENT_NAME = "Anna Nowak";

// ---------- static catalogue ----------

const TERMS: Term[] = [
  {
    id: "2025Z",
    name: { pl: "Semestr zimowy 2025/26", en: "Winter semester 2025/26" },
    startDate: "2025-10-01",
    endDate: "2026-02-15",
    isCurrent: true
  },
  {
    id: "2024L",
    name: { pl: "Semestr letni 2024/25", en: "Summer semester 2024/25" },
    startDate: "2025-02-17",
    endDate: "2025-09-30",
    isCurrent: false
  },
  {
    id: "2024Z",
    name: { pl: "Semestr zimowy 2024/25", en: "Winter semester 2024/25" },
    startDate: "2024-10-01",
    endDate: "2025-02-16",
    isCurrent: false
  }
];

interface MockUnit {
  unitId: string;
  classType: string;
  group: number | null;
  lecturers: string[];
}
interface MockCourse {
  id: string;
  termId: string;
  name: LangText;
  ects: number;
  units: MockUnit[];
}

const COURSES: MockCourse[] = [
  {
    id: "INF-PROG1",
    termId: "2025Z",
    name: { pl: "Programowanie obiektowe", en: "Object-oriented programming" },
    ects: 6,
    units: [
      { unitId: "U-5101", classType: "Wykład", group: 1, lecturers: ["dr hab. Piotr Zieliński"] },
      { unitId: "U-5102", classType: "Laboratorium", group: 2, lecturers: ["mgr inż. Katarzyna Wójcik"] }
    ]
  },
  {
    id: "INF-ALG",
    termId: "2025Z",
    name: { pl: "Algorytmy i struktury danych", en: "Algorithms and data structures" },
    ects: 6,
    units: [
      { unitId: "U-5201", classType: "Wykład", group: 1, lecturers: ["prof. dr hab. Marek Nowicki"] },
      { unitId: "U-5202", classType: "Ćwiczenia", group: 1, lecturers: ["dr Tomasz Lis"] }
    ]
  },
  {
    id: "INF-BD",
    termId: "2025Z",
    name: { pl: "Bazy danych", en: "Databases" },
    ects: 5,
    units: [
      { unitId: "U-5301", classType: "Wykład", group: 1, lecturers: ["dr Anna Maj"] },
      { unitId: "U-5302", classType: "Laboratorium", group: 2, lecturers: ["dr Anna Maj", "mgr inż. Jan Zając"] }
    ]
  },
  {
    id: "INF-MAT",
    termId: "2025Z",
    name: { pl: "Matematyka dyskretna", en: "Discrete mathematics" },
    ects: 5,
    units: [
      { unitId: "U-5401", classType: "Wykład", group: 1, lecturers: ["dr hab. Jan Wiśniewski"] },
      { unitId: "U-5402", classType: "Ćwiczenia", group: 2, lecturers: ["dr Ewa Kaczmarek"] }
    ]
  },
  {
    id: "INF-ANG",
    termId: "2025Z",
    name: { pl: "Język angielski B2", en: "English B2" },
    ects: 2,
    units: [{ unitId: "U-5501", classType: "Lektorat", group: 3, lecturers: ["mgr Emily Carter"] }]
  },
  {
    id: "INF-SO",
    termId: "2025Z",
    name: { pl: "Systemy operacyjne", en: "Operating systems" },
    ects: 4,
    units: [
      { unitId: "U-5601", classType: "Wykład", group: 1, lecturers: ["dr inż. Paweł Dąbrowski"] },
      { unitId: "U-5602", classType: "Laboratorium", group: 1, lecturers: ["mgr inż. Michał Kruk"] }
    ]
  },
  // past terms (course list only; used for grades and getCourses(termId))
  {
    id: "INF-WIN",
    termId: "2024Z",
    name: { pl: "Wstęp do informatyki", en: "Introduction to computer science" },
    ects: 4,
    units: [{ unitId: "U-4101", classType: "Wykład", group: 1, lecturers: ["dr Piotr Zieliński"] }]
  },
  {
    id: "INF-AM1",
    termId: "2024Z",
    name: { pl: "Analiza matematyczna I", en: "Calculus I" },
    ects: 6,
    units: [
      { unitId: "U-4201", classType: "Wykład", group: 1, lecturers: ["prof. dr hab. Jan Wiśniewski"] },
      { unitId: "U-4202", classType: "Ćwiczenia", group: 3, lecturers: ["dr Ewa Kaczmarek"] }
    ]
  },
  {
    id: "INF-PP",
    termId: "2024Z",
    name: { pl: "Podstawy programowania", en: "Programming fundamentals" },
    ects: 6,
    units: [
      { unitId: "U-4301", classType: "Wykład", group: 1, lecturers: ["dr Piotr Zieliński"] },
      { unitId: "U-4302", classType: "Laboratorium", group: 2, lecturers: ["mgr inż. Katarzyna Wójcik"] }
    ]
  },
  {
    id: "INF-AK",
    termId: "2024Z",
    name: { pl: "Architektura komputerów", en: "Computer architecture" },
    ects: 4,
    units: [{ unitId: "U-4401", classType: "Wykład", group: 1, lecturers: ["dr inż. Paweł Dąbrowski"] }]
  },
  {
    id: "INF-ANG1",
    termId: "2024Z",
    name: { pl: "Język angielski B1", en: "English B1" },
    ects: 2,
    units: [{ unitId: "U-4501", classType: "Lektorat", group: 3, lecturers: ["mgr Emily Carter"] }]
  },
  {
    id: "INF-AM2",
    termId: "2024L",
    name: { pl: "Analiza matematyczna II", en: "Calculus II" },
    ects: 6,
    units: [{ unitId: "U-3201", classType: "Wykład", group: 1, lecturers: ["prof. dr hab. Jan Wiśniewski"] }]
  },
  {
    id: "INF-AL",
    termId: "2024L",
    name: { pl: "Algebra liniowa", en: "Linear algebra" },
    ects: 5,
    units: [{ unitId: "U-3301", classType: "Wykład", group: 1, lecturers: ["dr hab. Ewa Kaczmarek"] }]
  },
  {
    id: "INF-PP2",
    termId: "2024L",
    name: { pl: "Programowanie strukturalne", en: "Structured programming" },
    ects: 6,
    units: [{ unitId: "U-3401", classType: "Laboratorium", group: 2, lecturers: ["mgr inż. Katarzyna Wójcik"] }]
  },
  {
    id: "INF-ASK",
    termId: "2024L",
    name: { pl: "Architektura systemów komputerowych", en: "Computer systems architecture" },
    ects: 4,
    units: [{ unitId: "U-3501", classType: "Wykład", group: 1, lecturers: ["dr inż. Paweł Dąbrowski"] }]
  },
  {
    id: "INF-ANG2",
    termId: "2024L",
    name: { pl: "Język angielski B1+", en: "English B1+" },
    ects: 2,
    units: [{ unitId: "U-3601", classType: "Lektorat", group: 3, lecturers: ["mgr Emily Carter"] }]
  }
];

interface MockGradeSeed {
  termId: string;
  courseId: string;
  classType: string | null;
  session: number;
  symbol: string;
  /** local Warsaw time */
  at: string;
  by: string;
  counts?: boolean;
  comment?: string;
}

const GRADE_SEEDS: MockGradeSeed[] = [
  // 2025Z (in progress)
  { termId: "2025Z", courseId: "INF-PROG1", classType: "Laboratorium", session: 1, symbol: "4.5", at: "2025-11-12 14:20:00", by: "mgr inż. Katarzyna Wójcik" },
  { termId: "2025Z", courseId: "INF-ALG", classType: "Ćwiczenia", session: 1, symbol: "4.0", at: "2025-11-19 12:05:00", by: "dr Tomasz Lis", comment: "Kolokwium 1: 78%" },
  { termId: "2025Z", courseId: "INF-BD", classType: "Laboratorium", session: 1, symbol: "5.0", at: "2025-12-03 13:40:00", by: "dr Anna Maj" },
  { termId: "2025Z", courseId: "INF-ANG", classType: "Lektorat", session: 1, symbol: "zal", at: "2025-12-10 11:35:00", by: "mgr Emily Carter", counts: false },
  // 2024L
  { termId: "2024L", courseId: "INF-AM2", classType: null, session: 1, symbol: "2.0", at: "2025-06-20 10:10:00", by: "prof. dr hab. Jan Wiśniewski" },
  { termId: "2024L", courseId: "INF-AM2", classType: null, session: 2, symbol: "3.5", at: "2025-09-05 09:45:00", by: "prof. dr hab. Jan Wiśniewski", comment: "Termin poprawkowy" },
  { termId: "2024L", courseId: "INF-AL", classType: null, session: 1, symbol: "4.0", at: "2025-06-24 12:30:00", by: "dr hab. Ewa Kaczmarek" },
  { termId: "2024L", courseId: "INF-PP2", classType: "Laboratorium", session: 1, symbol: "5.0", at: "2025-06-12 15:00:00", by: "mgr inż. Katarzyna Wójcik" },
  { termId: "2024L", courseId: "INF-ASK", classType: null, session: 1, symbol: "3.5", at: "2025-06-26 11:15:00", by: "dr inż. Paweł Dąbrowski" },
  { termId: "2024L", courseId: "INF-ANG2", classType: "Lektorat", session: 1, symbol: "zal", at: "2025-06-10 13:00:00", by: "mgr Emily Carter", counts: false },
  // 2024Z
  { termId: "2024Z", courseId: "INF-WIN", classType: null, session: 1, symbol: "4.5", at: "2025-01-28 10:20:00", by: "dr Piotr Zieliński" },
  { termId: "2024Z", courseId: "INF-AM1", classType: null, session: 1, symbol: "3.0", at: "2025-02-04 10:12:00", by: "prof. dr hab. Jan Wiśniewski" },
  { termId: "2024Z", courseId: "INF-PP", classType: "Laboratorium", session: 1, symbol: "4.0", at: "2025-01-22 16:00:00", by: "mgr inż. Katarzyna Wójcik" },
  { termId: "2024Z", courseId: "INF-AK", classType: null, session: 1, symbol: "4.0", at: "2025-02-06 12:00:00", by: "dr inż. Paweł Dąbrowski" },
  { termId: "2024Z", courseId: "INF-ANG1", classType: "Lektorat", session: 1, symbol: "zal", at: "2025-01-20 11:00:00", by: "mgr Emily Carter", counts: false }
];

interface Slot {
  weekday: number; // 1 = Monday
  start: string;
  end: string;
  unitId: string;
  room: string;
  building: string;
}

const WEEKLY_SLOTS: Slot[] = [
  { weekday: 1, start: "08:30", end: "10:00", unitId: "U-5201", room: "A-214", building: "Budynek A" },
  { weekday: 1, start: "12:30", end: "14:00", unitId: "U-5402", room: "B-105", building: "Budynek B" },
  { weekday: 2, start: "09:00", end: "10:30", unitId: "U-5301", room: "A-301", building: "Budynek A" },
  { weekday: 2, start: "10:45", end: "12:15", unitId: "U-5302", room: "Lab 3.12", building: "Budynek A" },
  { weekday: 3, start: "08:30", end: "10:00", unitId: "U-5601", room: "A-214", building: "Budynek A" },
  { weekday: 3, start: "10:15", end: "11:45", unitId: "U-5202", room: "B-104", building: "Budynek B" },
  { weekday: 3, start: "12:30", end: "14:00", unitId: "U-5102", room: "Lab 3.10", building: "Budynek A" },
  { weekday: 4, start: "09:00", end: "10:30", unitId: "U-5401", room: "A-301", building: "Budynek A" },
  { weekday: 4, start: "10:45", end: "12:15", unitId: "U-5602", room: "Lab 3.11", building: "Budynek A" },
  { weekday: 5, start: "10:00", end: "11:30", unitId: "U-5501", room: "C-12", building: "Budynek C" },
  { weekday: 5, start: "11:45", end: "13:15", unitId: "U-5101", room: "A-214", building: "Budynek A" }
];

// ---------- adapter ----------

function unitIndex(): Map<string, { course: MockCourse; unit: MockUnit }> {
  const m = new Map<string, { course: MockCourse; unit: MockUnit }>();
  for (const course of COURSES) for (const unit of course.units) m.set(unit.unitId, { course, unit });
  return m;
}

function paragraphs(...lines: string[]): string {
  return lines.map((l) => `<p>${l}</p>`).join("");
}

export function createUsosMockAdapter(deps: UsosAdapterDeps = {}): UsosAdapter {
  const now = deps.now ?? (() => new Date());
  const today = (): string => warsawDateOf(now());
  const units = unitIndex();
  const isoAt = (daysAgo: number, time: string): string => {
    const iso = warsawLocalToIso(`${addDays(today(), -daysAgo)} ${time}:00`);
    return iso ?? "";
  };

  const sent: Message[] = [];
  const readIds = new Set<string>();

  function buildMessages(): Message[] {
    const base = (
      n: number,
      from: string,
      subject: string,
      daysAgo: number,
      time: string,
      unread: boolean,
      body: string
    ): Message => ({
      id: `mock-msg-${1000 + n}`,
      source: "usos",
      subject,
      from,
      to: [STUDENT_NAME],
      date: isoAt(daysAgo, time),
      preview: body.replace(/<[^>]*>/g, " ").replace(/\s+/g, " ").trim().slice(0, 140),
      unread,
      url: null,
      bodyHtml: body,
      attachments: []
    });
    const all: Message[] = [
      base(1, "Dziekanat", "Potwierdzenie wpisu na semestr zimowy 2025/26", 1, "10:15", true,
        paragraphs("Szanowna Pani,", "potwierdzamy wpis na semestr zimowy roku akademickiego 2025/26 (Informatyka, studia I stopnia, semestr 5).", "Pozdrawiamy,<br>Dziekanat")),
      base(2, "Biuro ds. Studenckich", "Zmiana sali zajęć z Baz danych (wtorek)", 2, "15:42", true,
        paragraphs("Informujemy, że wykład z Baz danych w najbliższy wtorek odbędzie się w sali A-301 (zamiast A-214).", "Biuro ds. Studenckich")),
      base(3, "Dziekanat", "Przypomnienie: zaległa rata czesnego", 4, "09:05", false,
        paragraphs("Przypominamy o konieczności uregulowania raty czesnego 3/5. Brak wpłaty skutkuje naliczeniem odsetek ustawowych.", "Dane do przelewu znajdziesz w zakładce Płatności.")),
      base(4, "Biuro ds. Studenckich", "Zapisy na lektoraty na semestr letni", 9, "12:30", false,
        paragraphs("Ruszają zapisy na lektoraty na semestr letni 2025/26. Zapisy prowadzimy w USOSweb do końca stycznia.")),
      base(5, "Dziekanat", "Harmonogram sesji egzaminacyjnej zimowej 2025/26", 15, "08:50", false,
        paragraphs("W załączeniu (USOSweb → Komunikacja) przesyłamy harmonogram sesji zimowej. Sesja podstawowa: 26.01–08.02.2026, poprawkowa: 09.02–15.02.2026.")),
      base(6, "Biuro ds. Studenckich", "Wnioski o stypendium rektora - termin składania", 21, "14:10", false,
        paragraphs("Przypominamy, że wnioski o stypendium rektora na rok akademicki 2025/26 można składać do 15 października.", "Wniosek składasz elektronicznie w USOSweb."))
    ];
    return all;
  }

  const baseMessages = buildMessages();
  const allMessages = (): Message[] => [...sent, ...baseMessages].map((m) => ({ ...m, unread: m.unread && !readIds.has(m.id) }));

  return {
    async getProfile(): Promise<StudentProfile> {
      return {
        id: "mock-usos-1001",
        firstName: "Anna",
        lastName: "Nowak",
        email: "anna.nowak@stu.vistula.example",
        studentNumber: "61234",
        photoUrl: null,
        programmes: [
          {
            id: "INF-I-S",
            name: { pl: "Informatyka", en: "Computer Science" },
            status: "active",
            stage: "Studia I stopnia, stacjonarne, semestr 5"
          }
        ]
      };
    },

    async getTerms(): Promise<Term[]> {
      return structuredClone(TERMS);
    },

    async getCourses(termId?: string): Promise<CourseUnit[]> {
      const wanted = termId ?? MOCK_CURRENT_TERM;
      return COURSES.filter((c) => c.termId === wanted).flatMap((c) =>
        c.units.map((u) => ({
          id: u.unitId,
          courseId: c.id,
          courseName: { ...c.name },
          classType: u.classType,
          termId: c.termId,
          groupNumber: u.group,
          lecturers: [...u.lecturers],
          ects: c.ects,
          platonCourseId: null
        }))
      );
    },

    async getTimetable(from: string, to: string): Promise<TimetableEntry[]> {
      const f = toDateOnly(from);
      const t = toDateOnly(to);
      if (!f || !t || t < f) return [];
      const days = eachDay(f, t).slice(0, 366);
      const out: TimetableEntry[] = [];
      for (const date of days) {
        const wd = weekday(date);
        for (const slot of WEEKLY_SLOTS) {
          if (slot.weekday !== wd) continue;
          const found = units.get(slot.unitId);
          const start = warsawLocalToIso(`${date} ${slot.start}:00`);
          const end = warsawLocalToIso(`${date} ${slot.end}:00`);
          if (!found || !start || !end) continue;
          out.push({
            id: `mock-tt-${slot.unitId}-${date.replace(/-/g, "")}`,
            source: "usos",
            start,
            end,
            courseId: found.course.id,
            courseName: { ...found.course.name },
            classType: found.unit.classType,
            groupNumber: found.unit.group,
            room: slot.room,
            building: slot.building,
            lecturers: [...found.unit.lecturers],
            url: null
          });
        }
      }
      return out.sort((a, b) => a.start.localeCompare(b.start));
    },

    async getGrades(termIds?: string[]): Promise<Grade[]> {
      const courses = new Map(COURSES.map((c) => [c.id, c]));
      const grades: Grade[] = [];
      for (const s of GRADE_SEEDS) {
        if (termIds && termIds.length > 0 && !termIds.includes(s.termId)) continue;
        const course = courses.get(s.courseId);
        if (!course) continue;
        const info = describeGrade(s.symbol);
        grades.push({
          id: `mock-grade-${s.termId}-${s.courseId}-${(s.classType ?? "kurs").toLowerCase()}-${s.session}`,
          termId: s.termId,
          courseId: s.courseId,
          courseName: { ...course.name },
          classType: s.classType,
          examSession: s.session,
          valueSymbol: s.symbol,
          valueDescription: info.description,
          passes: info.passes,
          countsIntoAverage: s.counts ?? true,
          dateModified: warsawLocalToIso(s.at),
          gradedBy: s.by,
          comment: s.comment ?? null
        });
      }
      return grades.sort((a, b) => (b.dateModified ?? "").localeCompare(a.dateModified ?? ""));
    },

    async getPayments(): Promise<Payment[]> {
      const t = today();
      const account = "61109010140000071219812874";
      const installment = (n: number, dueOffset: number, paid: number, state: Payment["state"], interest: number | null): Payment => ({
        id: `mock-pay-2025Z-${n}`,
        type: "tuition",
        description: {
          pl: `Czesne za semestr zimowy 2025/26 - rata ${n}/5`,
          en: `Tuition, winter semester 2025/26 - instalment ${n}/5`
        },
        amount: 1250,
        paid,
        currency: "PLN",
        dueDate: addDays(t, dueOffset),
        state,
        interest,
        accountNumber: account
      });
      return [
        installment(1, -72, 1250, "paid", 0),
        installment(2, -40, 1250, "paid", 0),
        // 12 days late at 11.5% p.a.: 1250 * 0.115 / 365 * 12 = 4.73
        installment(3, -12, 0, "overdue", 4.73),
        installment(4, 18, 0, "unpaid", null),
        installment(5, 48, 0, "unpaid", null),
        {
          id: "mock-pay-legitymacja",
          type: "id_card",
          description: { pl: "Opłata za legitymację studencką", en: "Student ID card fee" },
          amount: 22,
          paid: 22,
          currency: "PLN",
          dueDate: addDays(t, -100),
          state: "paid",
          interest: null,
          accountNumber: account
        }
      ];
    },

    async listMessages(opts): Promise<MessageThread[]> {
      const offset = opts?.offset ?? 0;
      const limit = opts?.limit ?? 50;
      return allMessages()
        .slice(offset, offset + limit)
        .map(({ bodyHtml: _body, attachments: _att, ...thread }) => thread);
    },

    async getMessage(id: string): Promise<Message> {
      const msg = allMessages().find((m) => m.id === id);
      if (!msg) throw new AdapterUnavailableError(`Nie znaleziono wiadomości ${id}`);
      readIds.add(id);
      return { ...msg, unread: false };
    },

    async sendMessage(to: string[], subject: string, body: string): Promise<{ id: string }> {
      const id = `mock-msg-sent-${sent.length + 1}`;
      sent.unshift({
        id,
        source: "usos",
        subject,
        from: STUDENT_NAME,
        to: [...to],
        date: now().toISOString(),
        preview: body.slice(0, 140),
        unread: false,
        url: null,
        bodyHtml: `<p>${body.replace(/&/g, "&amp;").replace(/</g, "&lt;")}</p>`,
        attachments: []
      });
      return { id };
    },

    async getDeanOfficeContacts(): Promise<Contact[]> {
      return [
        {
          id: "mock-contact-dziekanat",
          name: "Dziekanat Wydziału Informatyki",
          role: "Dziekanat",
          email: "dziekanat.informatyka@vistula.example",
          phone: "+48 22 000 01 01",
          room: "Budynek A, pok. 105",
          hours: "pon-pt 9:00-15:00, śr do 17:00"
        },
        {
          id: "mock-contact-bss",
          name: "Biuro ds. Studenckich",
          role: "Obsługa studentów",
          email: "bss@vistula.example",
          phone: "+48 22 000 01 02",
          room: "Budynek A, parter",
          hours: "pon-pt 8:30-16:00"
        },
        {
          id: "mock-contact-kwestura",
          name: "Kwestura - rozliczenia czesnego",
          role: "Płatności",
          email: "kwestura@vistula.example",
          phone: "+48 22 000 01 03",
          room: "Budynek B, pok. 12",
          hours: "pon, śr, pt 10:00-14:00"
        },
        {
          id: "mock-contact-opiekun",
          name: "dr Tomasz Lis",
          role: "Opiekun roku",
          email: "tomasz.lis@vistula.example",
          phone: null,
          room: "Budynek A, pok. 311",
          hours: "wt 13:00-14:30 (po wcześniejszym umówieniu)"
        },
        {
          id: "mock-contact-it",
          name: "Pomoc techniczna USOS / Platon",
          role: "Wsparcie IT",
          email: "pomoc-it@vistula.example",
          phone: "+48 22 000 01 04",
          room: null,
          hours: "pon-pt 8:00-18:00"
        }
      ];
    },

    async getAnnouncements(limit = 20): Promise<Announcement[]> {
      const item = (n: number, title: string, daysAgo: number, author: string, category: string, body: string): Announcement => ({
        id: `mock-news-${n}`,
        source: "usos",
        title,
        bodyHtml: body,
        publishedAt: isoAt(daysAgo, "09:00"),
        author,
        category,
        url: null
      });
      const all: Announcement[] = [
        item(1, "Zapisy na seminaria dyplomowe", 2, "Dziekanat", "Dydaktyka",
          paragraphs("Zapisy na seminaria dyplomowe dla studentów semestru 5 rozpoczynają się w poniedziałek o 10:00 w USOSweb.")),
        item(2, "Zmiana godzin pracy dziekanatu w listopadzie", 5, "Biuro ds. Studenckich", "Organizacja",
          paragraphs("W dniach 3-4 listopada dziekanat jest nieczynny. Sprawy pilne: dziekanat.informatyka@vistula.example.")),
        item(3, "Stypendia rektora - wyniki", 11, "Komisja stypendialna", "Stypendia",
          paragraphs("Wyniki postępowania stypendialnego są dostępne w USOSweb w zakładce Stypendia.")),
        item(4, "Dzień otwarty biblioteki i szkolenie z baz naukowych", 16, "Biblioteka", "Wydarzenia",
          paragraphs("Zapraszamy na szkolenie z korzystania z baz naukowych w czwartek o 16:00 (sala C-12).")),
        item(5, "Harmonogram sesji zimowej 2025/26", 24, "Dziekanat", "Dydaktyka",
          paragraphs("Opublikowano harmonogram sesji egzaminacyjnej. Szczegóły w wiadomości od dziekanatu."))
      ];
      return all.slice(0, limit);
    }
  };
}
