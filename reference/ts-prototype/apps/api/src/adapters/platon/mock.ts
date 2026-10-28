import {
  AdapterUnavailableError,
  type Announcement,
  type LmsAssignment,
  type LmsCourse,
  type LmsEvent,
  type LmsGradeItem,
  type LmsModule,
  type LmsSection,
  type PlatonAdapter
} from "@vusos/shared";
import { normalizeBaseUrl } from "./transport.js";

export interface PlatonMockOptions {
  baseUrl?: string;
  /** Clock; all dates in the dataset are relative to it (so the demo never goes stale). */
  now?: () => Date;
}

const DAY = 86_400_000;
const HOUR = 3_600_000;

const MIME = {
  pdf: "application/pdf",
  pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
  zip: "application/zip",
  sql: "application/sql"
} as const;

interface FileDef {
  name: string;
  mime: string;
  size: number;
}

interface AssignDef {
  key: string;
  course: number; // index into COURSES
  name: string;
  dueDays: number; // relative to today 21:59Z
  submitted: boolean;
  grade?: { value: number; max: number };
  description: string;
}

interface QuizDef {
  key: string;
  course: number;
  name: string;
  closeDays: number;
}

interface CourseDef {
  id: number;
  short: string;
  full: string;
  teacher: string;
  progress: number | null;
  color: string;
  weeks: number; // 0..2 weekly sections between "Wprowadzenie" and "Materiały"
  lastAccessHours: number;
  material: FileDef[];
  link: { name: string; url: string };
}

const COURSES: readonly CourseDef[] = [
  {
    id: 3101, short: "INF-ALG-2025Z", full: "Algorytmy i struktury danych", teacher: "dr Anna Nowak",
    progress: 43, color: "#2563eb", weeks: 2, lastAccessHours: 5,
    material: [{ name: "Cormen_rozdzial_2.pdf", mime: MIME.pdf, size: 1_204_331 }],
    link: { name: "Visualgo - wizualizacje algorytmów", url: "https://visualgo.net/en" }
  },
  {
    id: 3102, short: "INF-BD-2025Z", full: "Bazy danych", teacher: "dr inż. Piotr Zieliński",
    progress: 31, color: "#0d9488", weeks: 2, lastAccessHours: 26,
    material: [
      { name: "Model_relacyjny.pdf", mime: MIME.pdf, size: 845_120 },
      { name: "Skrypt_SQL_laboratorium.sql", mime: MIME.sql, size: 6_410 }
    ],
    link: { name: "PostgreSQL - dokumentacja", url: "https://www.postgresql.org/docs/" }
  },
  {
    id: 3103, short: "INF-PROG-2025Z", full: "Programowanie obiektowe", teacher: "mgr Katarzyna Wójcik",
    progress: 55, color: "#7c3aed", weeks: 1, lastAccessHours: 3,
    material: [{ name: "Wzorce_projektowe.pdf", mime: MIME.pdf, size: 2_310_998 }],
    link: { name: "Refactoring Guru - wzorce", url: "https://refactoring.guru/pl/design-patterns" }
  },
  {
    id: 3104, short: "INF-SO-2025Z", full: "Systemy operacyjne", teacher: "dr hab. Marek Lewandowski",
    progress: 20, color: "#dc2626", weeks: 1, lastAccessHours: 72,
    material: [{ name: "Procesy_i_watki.pdf", mime: MIME.pdf, size: 1_540_776 }],
    link: { name: "The Linux man-pages", url: "https://man7.org/linux/man-pages/" }
  },
  {
    id: 3105, short: "MAT-DYS-2025Z", full: "Matematyka dyskretna", teacher: "prof. Ewa Kamińska",
    progress: 68, color: "#d97706", weeks: 0, lastAccessHours: 120,
    material: [{ name: "Logika_zdan.pdf", mime: MIME.pdf, size: 612_004 }],
    link: { name: "Zbiór zadań - kombinatoryka", url: "https://example.org/kombinatoryka" }
  },
  {
    id: 3106, short: "ANG-B2-2025Z", full: "Język angielski B2", teacher: "mgr Sarah Mitchell",
    progress: null, color: "#059669", weeks: 0, lastAccessHours: 48,
    material: [{ name: "Unit_4_vocabulary.pdf", mime: MIME.pdf, size: 402_118 }],
    link: { name: "Cambridge Dictionary", url: "https://dictionary.cambridge.org/" }
  }
];

const ASSIGNMENTS: readonly AssignDef[] = [
  { key: "alg-1", course: 0, name: "Zadanie 1: Sortowanie", dueDays: 3, submitted: false, description: "Zaimplementuj quicksort i mergesort oraz porównaj czasy działania." },
  { key: "alg-0", course: 0, name: "Zadanie 0: Rozgrzewka", dueDays: -8, submitted: true, grade: { value: 9, max: 10 }, description: "Proste zadanie na złożoność obliczeniową." },
  { key: "bd-erd", course: 1, name: "Projekt schematu ERD", dueDays: 6, submitted: false, description: "Zaprojektuj schemat ERD dla systemu rezerwacji sal." },
  { key: "bd-lab3", course: 1, name: "Laboratorium 3: SQL JOIN", dueDays: -2, submitted: false, description: "Zestaw zapytań z użyciem JOIN i GROUP BY." },
  { key: "prog-2", course: 2, name: "Zadanie 2: Dziedziczenie", dueDays: 1, submitted: true, description: "Hierarchia klas pojazdów z polimorfizmem." },
  { key: "prog-proj", course: 2, name: "Projekt: system biblioteczny", dueDays: 20, submitted: false, description: "Projekt zaliczeniowy w parach." },
  { key: "so-lab2", course: 3, name: "Sprawozdanie z lab 2: procesy", dueDays: 5, submitted: false, description: "Sprawozdanie w PDF, max 3 strony." },
  { key: "mat-3", course: 4, name: "Zestaw 3: indukcja", dueDays: 12, submitted: false, description: "Dowody indukcyjne." },
  { key: "ang-essay", course: 5, name: "Essay: My study plans", dueDays: 4, submitted: false, description: "Write 250 words about your study plans." }
];

const QUIZZES: readonly QuizDef[] = [
  { key: "alg-q1", course: 0, name: "Quiz: złożoność obliczeniowa", closeDays: 2 },
  { key: "mat-q1", course: 4, name: "Test z logiki zdań", closeDays: 9 }
];

/** Stable numeric id for a definition key (small, deterministic). */
function keyId(prefix: number, key: string): number {
  let h = 0;
  for (const ch of key) h = (h * 31 + ch.charCodeAt(0)) % 90_000;
  return prefix + h;
}

export function createPlatonMock(options: PlatonMockOptions = {}): PlatonAdapter {
  const base = normalizeBaseUrl(options.baseUrl ?? "https://platon.vistula.edu.pl/");
  const now = options.now ?? ((): Date => new Date());
  const t0 = now();
  const day0 = Date.UTC(t0.getUTCFullYear(), t0.getUTCMonth(), t0.getUTCDate());
  /** `days` after today's 00:00Z at 21:59Z (= 22:59/23:59 Warsaw, the usual Moodle deadline). */
  const deadline = (days: number): Date => new Date(day0 + days * DAY + 21 * HOUR + 59 * 60_000);
  const ago = (ms: number): Date => new Date(t0.getTime() - ms);

  const courses: LmsCourse[] = COURSES.map((c, i) => ({
    id: String(c.id),
    shortName: c.short,
    fullName: `${c.full} (${c.short})`,
    category: "Informatyka 2025/26",
    progress: c.progress,
    startDate: new Date(Date.UTC(2025, 9, 1)).toISOString(),
    endDate: new Date(Date.UTC(2026, 1, 14)).toISOString(),
    imageUrl: `data:image/svg+xml;utf8,${encodeURIComponent(
      `<svg xmlns="http://www.w3.org/2000/svg" width="400" height="200"><rect width="400" height="200" fill="${c.color}"/></svg>`
    )}`,
    url: `${base}course/view.php?id=${c.id}`,
    usosCourseId: `${c.short.split("-").slice(0, 2).join("-")}-${i + 1}`,
    lastAccess: ago(c.lastAccessHours * HOUR).toISOString()
  }));

  const assignId = (a: AssignDef): number => keyId(8000, a.key);
  const assignCmid = (a: AssignDef): number => keyId(412000, a.key);
  const quizId = (q: QuizDef): number => keyId(9000, q.key);
  const quizCmid = (q: QuizDef): number => keyId(413000, q.key);
  const courseOf = (idx: number): CourseDef => COURSES[idx] as CourseDef;

  const assignments: LmsAssignment[] = ASSIGNMENTS.map((a) => {
    const graded = a.grade !== undefined;
    return {
      id: String(assignId(a)),
      courseId: String(courseOf(a.course).id),
      courseName: `${courseOf(a.course).full} (${courseOf(a.course).short})`,
      name: a.name,
      dueDate: deadline(a.dueDays).toISOString(),
      cutoffDate: null,
      submitted: a.submitted,
      graded,
      grade: a.grade ? `${a.grade.value.toFixed(2).replace(".", ",")} / ${a.grade.max.toFixed(2).replace(".", ",")}` : null,
      url: `${base}mod/assign/view.php?id=${assignCmid(a)}`
    };
  }).sort((x, y) => (x.dueDate ?? "").localeCompare(y.dueDate ?? ""));

  // ---- course contents ----
  function buildContents(ci: number): LmsSection[] {
    const c = courseOf(ci);
    const cid = String(c.id);
    let seq = 0;
    const nextId = (): number => c.id * 100 + ++seq; // stable per course
    const sections: LmsSection[] = [];

    const addSection = (name: string, summary: string | null, build: (sectionId: string, sectionName: string) => LmsModule[]): void => {
      const id = String(c.id * 10 + sections.length);
      sections.push({ id, name, summaryHtml: summary, modules: build(id, name) });
    };
    const mod = (sectionId: string, sectionName: string, m: Partial<LmsModule> & Pick<LmsModule, "name" | "type">): LmsModule => {
      const id = nextId();
      return {
        id: String(id),
        courseId: cid,
        sectionId,
        sectionName,
        url: `${base}mod/${m.type}/view.php?id=${id}`,
        descriptionHtml: null,
        files: [],
        completed: null,
        visible: true,
        rawType: m.type,
        ...m
      };
    };
    const fileOf = (ctx: number, f: FileDef, modifiedDaysAgo: number): LmsModule["files"][number] => ({
      name: f.name,
      url: `${base}pluginfile.php/${ctx}/mod_resource/content/1/${encodeURIComponent(f.name)}`,
      mimeType: f.mime,
      size: f.size,
      modified: ago(modifiedDaysAgo * DAY).toISOString()
    });

    addSection("Wprowadzenie", `<p>Witamy na kursie <b>${c.full}</b>. Prowadzący: ${c.teacher}.</p>`, (sid, sn) => [
      mod(sid, sn, { name: "Ogłoszenia", type: "forum", descriptionHtml: "<p>Ogłoszenia prowadzącego</p>" }),
      (() => {
        const m = mod(sid, sn, { name: "Sylabus", type: "resource", completed: true });
        m.files = [fileOf(Number(m.id), { name: `Sylabus_${c.short}.pdf`, mime: MIME.pdf, size: 183_422 }, 40)];
        return m;
      })()
    ]);

    for (let w = 1; w <= c.weeks; w++) {
      addSection(`Tydzień ${w}`, null, (sid, sn) => {
        const folder = mod(sid, sn, { name: `Slajdy z wykładu ${w}`, type: "folder", completed: w === 1 });
        folder.files = [
          fileOf(Number(folder.id), { name: `Wyklad_0${w}.pptx`, mime: MIME.pptx, size: 2_411_023 + w * 1000 }, 30 - w * 7),
          fileOf(Number(folder.id), { name: `Cwiczenia_0${w}.pdf`, mime: MIME.pdf, size: 311_902 + w * 100 }, 28 - w * 7)
        ];
        const link = mod(sid, sn, { name: c.link.name, type: "url", url: c.link.url });
        return [folder, link];
      });
    }

    addSection("Materiały", null, (sid, sn) =>
      c.material.map((f) => {
        const m = mod(sid, sn, { name: f.name.replace(/\.[^.]+$/, "").replace(/_/g, " "), type: "resource", completed: false });
        m.files = [fileOf(Number(m.id), f, 10)];
        return m;
      })
    );

    addSection("Zadania", null, (sid, sn) => {
      const out: LmsModule[] = [];
      for (const a of ASSIGNMENTS.filter((x) => x.course === ci)) {
        out.push({
          id: String(assignCmid(a)),
          courseId: cid,
          sectionId: sid,
          sectionName: sn,
          name: a.name,
          type: "assign",
          rawType: "assign",
          url: `${base}mod/assign/view.php?id=${assignCmid(a)}`,
          descriptionHtml: `<p>${a.description}</p>`,
          files: [],
          completed: a.submitted,
          visible: true
        });
      }
      for (const q of QUIZZES.filter((x) => x.course === ci)) {
        out.push({
          id: String(quizCmid(q)),
          courseId: cid,
          sectionId: sid,
          sectionName: sn,
          name: q.name,
          type: "quiz",
          rawType: "quiz",
          url: `${base}mod/quiz/view.php?id=${quizCmid(q)}`,
          descriptionHtml: null,
          files: [],
          completed: false,
          visible: true
        });
      }
      if (ci === 5) out.push(mod(sid, sn, { name: "Discussion forum: Study abroad", type: "forum" }));
      return out;
    });
    return sections;
  }
  const contents = new Map<string, LmsSection[]>(COURSES.map((c, i) => [String(c.id), buildContents(i)]));

  // ---- events ----
  const allEvents: LmsEvent[] = [];
  for (const a of ASSIGNMENTS) {
    if (a.submitted) continue;
    const c = courseOf(a.course);
    allEvents.push({
      id: String(7_000_000 + assignId(a)),
      courseId: String(c.id),
      courseName: `${c.full} (${c.short})`,
      name: `${a.name} - termin oddania`,
      type: "due",
      moduleType: "assign",
      start: deadline(a.dueDays).toISOString(),
      end: null,
      url: `${base}mod/assign/view.php?id=${assignCmid(a)}`,
      actionName: "Dodaj zgłoszenie"
    });
  }
  for (const q of QUIZZES) {
    const c = courseOf(q.course);
    allEvents.push({
      id: String(7_500_000 + quizId(q)),
      courseId: String(c.id),
      courseName: `${c.full} (${c.short})`,
      name: `${q.name} - zamknięcie`,
      type: "close",
      moduleType: "quiz",
      start: deadline(q.closeDays).toISOString(),
      end: null,
      url: `${base}mod/quiz/view.php?id=${quizCmid(q)}`,
      actionName: "Podejdź do quizu"
    });
  }
  allEvents.push(
    {
      id: "7900001", courseId: String(courseOf(1).id), courseName: `${courseOf(1).full} (${courseOf(1).short})`,
      name: "Konsultacje online (MS Teams)", type: "course", moduleType: null,
      start: new Date(day0 + 8 * DAY + 14 * HOUR).toISOString(), end: new Date(day0 + 8 * DAY + 15 * HOUR).toISOString(),
      url: `${base}calendar/view.php?view=day`, actionName: null
    },
    {
      id: "7900002", courseId: String(courseOf(3).id), courseName: `${courseOf(3).full} (${courseOf(3).short})`,
      name: "Kolokwium zaliczeniowe - sala 204", type: "course", moduleType: null,
      start: new Date(day0 + 13 * DAY + 9 * HOUR).toISOString(), end: new Date(day0 + 13 * DAY + 10.5 * HOUR).toISOString(),
      url: `${base}calendar/view.php?view=day`, actionName: null
    }
  );
  allEvents.sort((a, b) => a.start.localeCompare(b.start));

  // ---- grades ----
  const gradesByCourse = new Map<string, LmsGradeItem[]>();
  COURSES.forEach((c, ci) => {
    const cid = String(c.id);
    const items: LmsGradeItem[] = [];
    let total = 0;
    let totalMax = 0;
    let latest: string | null = null;
    const add = (id: number, name: string, type: string, value: number | null, max: number, gradedDaysAgo: number, feedback: string | null): void => {
      const dateGraded = value !== null ? ago(gradedDaysAgo * DAY).toISOString() : null;
      if (value !== null) {
        total += value;
        totalMax += max;
        if (!latest || (dateGraded && dateGraded > latest)) latest = dateGraded;
      }
      items.push({
        id: String(id),
        courseId: cid,
        itemName: name,
        itemType: type,
        gradeFormatted: value !== null ? value.toFixed(2).replace(".", ",") : null,
        gradeRaw: value,
        gradeMax: max,
        percentage: value !== null ? `${((value / max) * 100).toFixed(2).replace(".", ",")} %` : null,
        feedbackHtml: feedback,
        dateGraded
      });
    };
    for (const a of ASSIGNMENTS.filter((x) => x.course === ci)) {
      add(keyId(123_000, a.key), a.name, "mod", a.grade?.value ?? null, a.grade?.max ?? 10, 6, a.grade ? "<p>Dobra robota.</p>" : null);
    }
    for (const q of QUIZZES.filter((x) => x.course === ci)) add(keyId(124_000, q.key), q.name, "mod", null, 10, 0, null);
    if (ci === 1) add(125_001, "Kolokwium 1", "manual", 17, 20, 12, "<p>Zabrakło normalizacji w zad. 3.</p>");
    if (ci === 4) add(125_002, "Aktywność na ćwiczeniach", "manual", 4, 5, 3, null);
    const maxForTotal = items.reduce((s, i) => s + (i.gradeMax ?? 0), 0);
    items.push({
      id: String(126_000 + c.id),
      courseId: cid,
      itemName: "Suma końcowa",
      itemType: "course",
      gradeFormatted: totalMax > 0 ? total.toFixed(2).replace(".", ",") : null,
      gradeRaw: totalMax > 0 ? total : null,
      gradeMax: maxForTotal,
      percentage: totalMax > 0 ? `${((total / maxForTotal) * 100).toFixed(2).replace(".", ",")} %` : null,
      feedbackHtml: null,
      dateGraded: latest
    });
    gradesByCourse.set(cid, items);
  });

  // ---- announcements ----
  const announcements: Announcement[] = [
    {
      id: "platon:15001", source: "platon", title: "Zmiana godzin pracy dziekanatu",
      bodyHtml: "<p>Od poniedziałku dziekanat Wydziału Informatyki jest czynny w godzinach 9:00-15:00.</p>",
      publishedAt: ago(1 * DAY + 2 * HOUR).toISOString(), author: "Dziekanat Informatyki", category: "Aktualności",
      url: `${base}mod/forum/discuss.php?d=15001`
    },
    {
      id: "platon:16001", source: "platon", title: "Przesunięcie wykładu z algorytmów",
      bodyHtml: "<p>Wykład w czwartek odbędzie się o 12:00 zamiast 10:00 (sala 101).</p>",
      publishedAt: ago(2 * DAY + 5 * HOUR).toISOString(), author: "dr Anna Nowak", category: `Ogłoszenia (${courseOf(0).short})`,
      url: `${base}mod/forum/discuss.php?d=16001`
    },
    {
      id: "platon:15002", source: "platon", title: "Rejestracja na lektoraty",
      bodyHtml: "<p>Rejestracja na lektoraty językowe trwa do piątku do godziny 23:59.</p>",
      publishedAt: ago(5 * DAY).toISOString(), author: "Studium Języków Obcych", category: "Aktualności",
      url: `${base}mod/forum/discuss.php?d=15002`
    },
    {
      id: "platon:15003", source: "platon", title: "Planowana przerwa serwisowa Platona",
      bodyHtml: "<p>W sobotę w godzinach 2:00-4:00 platforma będzie niedostępna.</p>",
      publishedAt: ago(9 * DAY).toISOString(), author: "Centrum Informatyczne", category: "Aktualności",
      url: `${base}mod/forum/discuss.php?d=15003`
    }
  ];

  return {
    async getSiteInfo() {
      return { userId: 48213, fullName: "Jan Kowalski", siteName: "Platon (demo)", release: "4.5.2 (demo)" };
    },
    async getCourses() {
      return structuredClone(courses);
    },
    async getCourseContents(courseId) {
      const c = contents.get(courseId);
      if (!c) throw new AdapterUnavailableError(`Unknown course ${courseId}`);
      return structuredClone(c);
    },
    async getAssignments() {
      return structuredClone(assignments);
    },
    async getUpcomingEvents(days = 14) {
      const from = t0.getTime();
      const to = from + days * DAY;
      return structuredClone(allEvents.filter((e) => Date.parse(e.start) >= from && Date.parse(e.start) <= to));
    },
    async getGrades(courseId) {
      return structuredClone(gradesByCourse.get(courseId) ?? []);
    },
    async getAnnouncements(limit = 20) {
      return structuredClone(announcements.slice(0, limit));
    },
    async getFileDownload(url) {
      return { url, headers: {} };
    }
  };
}
