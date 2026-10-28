import { z } from "zod";

// ---------- Common ----------
export const SourceSystem = z.enum(["usos", "platon"]);
export type SourceSystem = z.infer<typeof SourceSystem>;

export const ConnectionStatus = z.enum(["disconnected", "connected", "expired", "error"]);
export type ConnectionStatus = z.infer<typeof ConnectionStatus>;

export const LangText = z.object({ pl: z.string(), en: z.string().optional() });
export type LangText = z.infer<typeof LangText>;

// ---------- User / profile ----------
export const StudentProfile = z.object({
  id: z.string(),
  firstName: z.string(),
  lastName: z.string(),
  email: z.string().email().nullable(),
  studentNumber: z.string().nullable(),
  photoUrl: z.string().url().nullable(),
  programmes: z.array(
    z.object({
      id: z.string(),
      name: LangText,
      status: z.string().nullable(),
      stage: z.string().nullable()
    })
  )
});
export type StudentProfile = z.infer<typeof StudentProfile>;

// ---------- Terms / courses ----------
export const Term = z.object({
  id: z.string(),
  name: LangText,
  startDate: z.string(),
  endDate: z.string(),
  isCurrent: z.boolean()
});
export type Term = z.infer<typeof Term>;

export const CourseUnit = z.object({
  id: z.string(),
  courseId: z.string(),
  courseName: LangText,
  classType: z.string(),
  termId: z.string(),
  groupNumber: z.number().nullable(),
  lecturers: z.array(z.string()),
  ects: z.number().nullable(),
  platonCourseId: z.string().nullable()
});
export type CourseUnit = z.infer<typeof CourseUnit>;

// ---------- Timetable ----------
export const TimetableEntry = z.object({
  id: z.string(),
  source: SourceSystem,
  start: z.string(),
  end: z.string(),
  courseId: z.string().nullable(),
  courseName: LangText,
  classType: z.string().nullable(),
  groupNumber: z.number().nullable(),
  room: z.string().nullable(),
  building: z.string().nullable(),
  lecturers: z.array(z.string()),
  url: z.string().nullable()
});
export type TimetableEntry = z.infer<typeof TimetableEntry>;

// ---------- Grades ----------
export const Grade = z.object({
  id: z.string(),
  termId: z.string(),
  courseId: z.string(),
  courseName: LangText,
  classType: z.string().nullable(),
  examSession: z.number().nullable(),
  valueSymbol: z.string(),
  valueDescription: LangText.nullable(),
  passes: z.boolean().nullable(),
  countsIntoAverage: z.boolean(),
  dateModified: z.string().nullable(),
  gradedBy: z.string().nullable(),
  comment: z.string().nullable()
});
export type Grade = z.infer<typeof Grade>;

export const TermGrades = z.object({
  term: Term,
  grades: z.array(Grade),
  average: z.number().nullable()
});
export type TermGrades = z.infer<typeof TermGrades>;

// ---------- Payments ----------
export const Payment = z.object({
  id: z.string(),
  type: z.string(),
  description: LangText,
  amount: z.number(),
  paid: z.number(),
  currency: z.string(),
  dueDate: z.string().nullable(),
  state: z.enum(["unpaid", "partially_paid", "paid", "overdue", "cancelled"]),
  interest: z.number().nullable(),
  accountNumber: z.string().nullable()
});
export type Payment = z.infer<typeof Payment>;

export const PaymentsSummary = z.object({
  payments: z.array(Payment),
  totalDue: z.number(),
  totalOverdue: z.number(),
  currency: z.string()
});
export type PaymentsSummary = z.infer<typeof PaymentsSummary>;

// ---------- Messages / dziekanat ----------
export const MessageThread = z.object({
  id: z.string(),
  source: SourceSystem,
  subject: z.string(),
  from: z.string(),
  to: z.array(z.string()),
  date: z.string(),
  preview: z.string(),
  unread: z.boolean(),
  url: z.string().nullable()
});
export type MessageThread = z.infer<typeof MessageThread>;

export const Message = MessageThread.extend({
  bodyHtml: z.string(),
  attachments: z.array(z.object({ name: z.string(), url: z.string(), size: z.number().nullable() }))
});
export type Message = z.infer<typeof Message>;

export const Contact = z.object({
  id: z.string(),
  name: z.string(),
  role: z.string(),
  email: z.string().nullable(),
  phone: z.string().nullable(),
  room: z.string().nullable(),
  hours: z.string().nullable()
});
export type Contact = z.infer<typeof Contact>;

// ---------- Announcements / news ----------
export const Announcement = z.object({
  id: z.string(),
  source: SourceSystem,
  title: z.string(),
  bodyHtml: z.string(),
  publishedAt: z.string(),
  author: z.string().nullable(),
  category: z.string().nullable(),
  url: z.string().nullable()
});
export type Announcement = z.infer<typeof Announcement>;

// ---------- Platon (Moodle) ----------
export const LmsCourse = z.object({
  id: z.string(),
  shortName: z.string(),
  fullName: z.string(),
  category: z.string().nullable(),
  progress: z.number().nullable(),
  startDate: z.string().nullable(),
  endDate: z.string().nullable(),
  imageUrl: z.string().nullable(),
  url: z.string(),
  usosCourseId: z.string().nullable(),
  lastAccess: z.string().nullable()
});
export type LmsCourse = z.infer<typeof LmsCourse>;

export const LmsModuleType = z.enum([
  "resource", "folder", "url", "page", "book", "assign", "quiz", "forum", "label", "lesson", "other"
]);
export type LmsModuleType = z.infer<typeof LmsModuleType>;

export const LmsModule = z.object({
  id: z.string(),
  courseId: z.string(),
  sectionId: z.string(),
  sectionName: z.string(),
  name: z.string(),
  type: LmsModuleType,
  rawType: z.string(),
  url: z.string().nullable(),
  descriptionHtml: z.string().nullable(),
  files: z.array(
    z.object({
      name: z.string(),
      url: z.string(),
      mimeType: z.string().nullable(),
      size: z.number().nullable(),
      modified: z.string().nullable()
    })
  ),
  completed: z.boolean().nullable(),
  visible: z.boolean()
});
export type LmsModule = z.infer<typeof LmsModule>;

export const LmsSection = z.object({
  id: z.string(),
  name: z.string(),
  summaryHtml: z.string().nullable(),
  modules: z.array(LmsModule)
});
export type LmsSection = z.infer<typeof LmsSection>;

export const LmsAssignment = z.object({
  id: z.string(),
  courseId: z.string(),
  courseName: z.string(),
  name: z.string(),
  dueDate: z.string().nullable(),
  cutoffDate: z.string().nullable(),
  submitted: z.boolean(),
  graded: z.boolean(),
  grade: z.string().nullable(),
  url: z.string()
});
export type LmsAssignment = z.infer<typeof LmsAssignment>;

export const LmsEvent = z.object({
  id: z.string(),
  courseId: z.string().nullable(),
  courseName: z.string().nullable(),
  name: z.string(),
  type: z.string(),
  moduleType: z.string().nullable(),
  start: z.string(),
  end: z.string().nullable(),
  url: z.string().nullable(),
  actionName: z.string().nullable()
});
export type LmsEvent = z.infer<typeof LmsEvent>;

export const LmsGradeItem = z.object({
  id: z.string(),
  courseId: z.string(),
  itemName: z.string(),
  itemType: z.string(),
  gradeFormatted: z.string().nullable(),
  gradeRaw: z.number().nullable(),
  gradeMax: z.number().nullable(),
  percentage: z.string().nullable(),
  feedbackHtml: z.string().nullable(),
  dateGraded: z.string().nullable()
});
export type LmsGradeItem = z.infer<typeof LmsGradeItem>;

// ---------- Dashboard (aggregate) ----------
export const DashboardData = z.object({
  profile: StudentProfile.nullable(),
  todayClasses: z.array(TimetableEntry),
  upcomingDeadlines: z.array(LmsEvent),
  recentGrades: z.array(Grade),
  paymentsDue: z.array(Payment),
  unreadMessages: z.number(),
  announcements: z.array(Announcement),
  connections: z.record(SourceSystem, ConnectionStatus)
});
export type DashboardData = z.infer<typeof DashboardData>;
