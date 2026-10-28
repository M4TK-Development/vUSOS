import type {
  Announcement, Contact, CourseUnit, Grade, LmsAssignment, LmsCourse, LmsEvent, LmsGradeItem,
  LmsSection, Message, MessageThread, Payment, StudentProfile, Term, TimetableEntry
} from "./domain.js";

/**
 * Credentials are stored per user, encrypted at rest. The shape depends on the mode.
 */
export type UsosCredentials =
  | { mode: "api"; accessToken: string; accessTokenSecret: string; baseUrl: string }
  | { mode: "web"; cookies: string; baseUrl: string }
  | { mode: "mock" };

export type PlatonCredentials =
  | { mode: "ws"; wstoken: string; privateToken?: string; baseUrl: string; userId?: number }
  | { mode: "web"; cookies: string; sesskey: string; baseUrl: string; userId?: number }
  | { mode: "mock" };

export class AdapterAuthError extends Error {
  readonly code = "ADAPTER_AUTH" as const;
  constructor(message = "Session expired or not connected") {
    super(message);
  }
}

export class AdapterUnavailableError extends Error {
  readonly code = "ADAPTER_UNAVAILABLE" as const;
}

/** USOS: dziekanat, oceny, plan, płatności, komunikacja, ogłoszenia. */
export interface UsosAdapter {
  getProfile(): Promise<StudentProfile>;
  getTerms(): Promise<Term[]>;
  getCourses(termId?: string): Promise<CourseUnit[]>;
  getTimetable(from: string, to: string): Promise<TimetableEntry[]>;
  getGrades(termIds?: string[]): Promise<Grade[]>;
  getPayments(): Promise<Payment[]>;
  listMessages(opts?: { limit?: number; offset?: number }): Promise<MessageThread[]>;
  getMessage(id: string): Promise<Message>;
  sendMessage(to: string[], subject: string, body: string): Promise<{ id: string }>;
  getDeanOfficeContacts(): Promise<Contact[]>;
  getAnnouncements(limit?: number): Promise<Announcement[]>;
}

/** Platon (Moodle): kursy, materiały, zadania, terminy, oceny z LMS. */
export interface PlatonAdapter {
  getSiteInfo(): Promise<{ userId: number; fullName: string; siteName: string; release: string | null }>;
  getCourses(): Promise<LmsCourse[]>;
  getCourseContents(courseId: string): Promise<LmsSection[]>;
  getAssignments(): Promise<LmsAssignment[]>;
  getUpcomingEvents(days?: number): Promise<LmsEvent[]>;
  getGrades(courseId: string): Promise<LmsGradeItem[]>;
  getAnnouncements(limit?: number): Promise<Announcement[]>;
  /** Returns an authenticated URL/headers to download a file through the BFF. */
  getFileDownload(url: string): Promise<{ url: string; headers: Record<string, string> }>;
}
