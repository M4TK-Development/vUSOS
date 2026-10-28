/**
 * Lenient zod schemas for raw USOS API JSON. Everything optional/unknown on purpose: field names come from
 * the public USOS API documentation and have NOT been checked against the Vistula installation
 * (see TODO(verify) markers). Mappers skip entries that fail to parse instead of throwing.
 */
import { z } from "zod";

/** USOS ids arrive as strings or numbers depending on the method. */
export const RawId = z.union([z.string(), z.number()]).transform((v) => String(v));
const NumLike = z.union([z.number(), z.string()]);
/** LangText-ish: string | {pl,en} | null - converted by toLangText(). */
const TextLike = z.unknown();

export const RawStudentProgramme = z
  .object({
    id: RawId,
    programme: z
      .object({
        id: RawId.nullish(),
        description: TextLike,
        level_of_studies: TextLike,
        mode_of_studies: TextLike
      })
      .passthrough()
      .nullish(),
    status: z.string().nullish(),
    stage: z.string().nullish()
  })
  .passthrough();

export const RawUser = z
  .object({
    id: RawId,
    first_name: z.string().nullish(),
    last_name: z.string().nullish(),
    email: z.string().nullish(),
    student_number: NumLike.nullish(),
    photo_urls: z.record(z.string(), z.string().nullable()).nullish(),
    student_programmes: z.array(RawStudentProgramme).nullish()
  })
  .passthrough();
export type RawUser = z.infer<typeof RawUser>;

export const RawTerm = z
  .object({
    id: RawId,
    name: TextLike,
    start_date: z.string(),
    end_date: z.string(),
    finish_date: z.string().nullish(),
    is_active: z.boolean().nullish()
  })
  .passthrough();
export type RawTerm = z.infer<typeof RawTerm>;

export const RawLecturer = z
  .object({ id: RawId.nullish(), first_name: z.string().nullish(), last_name: z.string().nullish() })
  .passthrough();

export const RawUserGroup = z
  .object({
    course_unit_id: RawId,
    group_number: NumLike.nullish(),
    class_type: TextLike,
    lecturers: z.array(RawLecturer).nullish()
  })
  .passthrough();

export const RawCourseEdition = z
  .object({
    course_id: RawId,
    course_name: TextLike,
    term_id: RawId.nullish(),
    user_groups: z.array(RawUserGroup).nullish()
  })
  .passthrough();
export type RawCourseEdition = z.infer<typeof RawCourseEdition>;

/** services/courses/user: course_editions is a dict term_id -> list (docs) but we also accept a flat list. */
export const RawCoursesUser = z
  .object({
    course_editions: z.union([z.record(z.string(), z.array(z.unknown())), z.array(z.unknown())]).nullish(),
    terms: z.array(z.unknown()).nullish()
  })
  .passthrough();

export const RawActivity = z
  .object({
    type: z.string().nullish(),
    start_time: z.string(),
    end_time: z.string(),
    name: TextLike,
    url: z.string().nullish(),
    unit_id: RawId.nullish(),
    course_id: RawId.nullish(),
    course_name: TextLike,
    classtype_name: TextLike,
    group_number: NumLike.nullish(),
    room_number: z.string().nullish(),
    building_name: TextLike,
    lecturer_ids: z.array(RawId).nullish()
  })
  .passthrough();
export type RawActivity = z.infer<typeof RawActivity>;

export const RawGrade = z
  .object({
    value_symbol: z.string().nullish(),
    value_description: TextLike,
    passes: z.boolean().nullish(),
    counts_into_average: z.union([z.boolean(), z.string()]).nullish(),
    exam_session_number: NumLike.nullish(),
    date_modified: z.string().nullish(),
    modification_author: z
      .object({ first_name: z.string().nullish(), last_name: z.string().nullish() })
      .passthrough()
      .nullish(),
    comment: z.string().nullish()
  })
  .passthrough();
export type RawGrade = z.infer<typeof RawGrade>;

export const RawPayment = z
  .object({
    id: RawId,
    type: z.string().nullish(),
    description: TextLike,
    total_amount: NumLike.nullish(),
    saldo_amount: NumLike.nullish(),
    state: z.string().nullish(),
    payment_deadline: z.string().nullish(),
    interest: NumLike.nullish(),
    account_number: z.string().nullish(),
    currency: z.string().nullish()
  })
  .passthrough();
export type RawPayment = z.infer<typeof RawPayment>;

export const RawMailMessage = z
  .object({
    id: RawId,
    subject: z.string().nullish(),
    sender: z.unknown(),
    recipients: z.array(z.unknown()).nullish(),
    date: z.string().nullish(),
    is_read: z.boolean().nullish(),
    preview: z.string().nullish(),
    body: z.string().nullish(),
    attachments: z
      .array(z.object({ name: z.string(), url: z.string(), size: NumLike.nullish() }).passthrough())
      .nullish()
  })
  .passthrough();
export type RawMailMessage = z.infer<typeof RawMailMessage>;

export const RawFaculty = z
  .object({
    id: RawId,
    name: TextLike,
    postal_address: z.string().nullish(),
    phone_numbers: z.array(z.string()).nullish(),
    homepage_url: z.string().nullish()
  })
  .passthrough();
export type RawFaculty = z.infer<typeof RawFaculty>;

export const RawNewsArticle = z
  .object({
    id: RawId,
    title: TextLike,
    content: TextLike,
    publication_date: z.string().nullish(),
    author: z.unknown(),
    category: TextLike,
    url: z.string().nullish()
  })
  .passthrough();
export type RawNewsArticle = z.infer<typeof RawNewsArticle>;
