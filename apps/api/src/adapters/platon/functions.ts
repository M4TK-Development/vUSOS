import {
  AdapterAuthError,
  AdapterUnavailableError,
  type Announcement,
  type LmsAssignment,
  type LmsCourse,
  type LmsEvent,
  type LmsGradeItem,
  type LmsModule,
  type LmsModuleType,
  type LmsSection,
  type PlatonAdapter
} from "@vusos/shared";
import type { z } from "zod";
import {
  RawAssignments,
  RawContents,
  RawDiscussions,
  RawEnrolledCourses,
  RawEvents,
  RawModule,
  RawForums,
  RawGradeItems,
  RawSiteInfo,
  RawSubmissionStatus
} from "./raw.js";
import { isSameOrigin, normalizeBaseUrl, type MoodleTransport } from "./transport.js";

export interface PlatonFunctionsOptions {
  baseUrl: string;
  /** Known user id (from stored credentials); avoids a site-info round trip when core_webservice_get_site_info is unavailable. */
  userId?: number | undefined;
  /** Clock, injectable for tests. */
  now?: () => Date;
  /** Course ids whose news forum is included in announcements. Default: [1] (site news). */
  newsCourseIds?: number[];
  /** Max assignments for which a per-assignment submission status is fetched. Default 20. */
  maxSubmissionStatusCalls?: number;
  /**
   * When true, file URLs in course contents are rewritten through the transport (ws: token appended to the URL).
   * Default false: the URL stays token-free and the client downloads via the BFF (`getFileDownload`), so the
   * wstoken never reaches the browser.
   */
  embedFileAuth?: boolean;
}

const MODULE_TYPES: Readonly<Record<string, LmsModuleType>> = {
  resource: "resource",
  folder: "folder",
  url: "url",
  page: "page",
  book: "book",
  assign: "assign",
  quiz: "quiz",
  forum: "forum",
  label: "label",
  lesson: "lesson"
};

export function toModuleType(modname: string): LmsModuleType {
  return MODULE_TYPES[modname] ?? "other";
}

/** Unix seconds -> ISO string; 0/null/undefined -> null. */
export function isoFromUnix(ts: number | null | undefined): string | null {
  return ts && ts > 0 ? new Date(ts * 1000).toISOString() : null;
}

/** Removes Moodle's `@@PLUGINFILE@@/` placeholder (it has no resolvable meaning outside the editing context). */
export function stripPluginfilePlaceholders(html: string | null | undefined): string | null {
  if (!html) return null;
  const cleaned = html.replace(/@@PLUGINFILE@@\/?/g, "");
  return cleaned.trim() === "" ? null : cleaned;
}

function stripTags(html: string): string {
  return html
    .replace(/<[^>]*>/g, "")
    .replace(/&nbsp;/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function emptyToNull(s: string | null | undefined): string | null {
  return s && s.trim() !== "" ? s : null;
}

/** Moodle uses "-" for "no grade". */
function dashToNull(s: string | null | undefined): string | null {
  const v = emptyToNull(s)?.trim();
  return v && v !== "-" ? v : null;
}

function parseResponse<S extends z.ZodTypeAny>(schema: S, data: unknown, fn: string): z.infer<S> {
  const r = schema.safeParse(data);
  if (!r.success) {
    const issue = r.error.issues[0];
    throw new AdapterUnavailableError(
      `Unexpected response from Platon (${fn}): ${issue ? `${issue.path.join(".")}: ${issue.message}` : "invalid"}`
    );
  }
  return r.data as z.infer<S>;
}

async function mapLimit<T, R>(items: readonly T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const worker = async (): Promise<void> => {
    for (;;) {
      const i = next++;
      if (i >= items.length) return;
      results[i] = await fn(items[i] as T);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

export function createPlatonFunctions(transport: MoodleTransport, opts: PlatonFunctionsOptions): PlatonAdapter {
  const base = normalizeBaseUrl(opts.baseUrl);
  const now = opts.now ?? ((): Date => new Date());
  const newsCourseIds = opts.newsCourseIds ?? [1];
  const maxStatusCalls = opts.maxSubmissionStatusCalls ?? 20;
  const courseUrl = (id: number | string): string => `${base}course/view.php?id=${id}`;

  let siteInfoPromise: Promise<z.infer<typeof RawSiteInfo>> | undefined;
  const loadSiteInfo = (): Promise<z.infer<typeof RawSiteInfo>> => {
    siteInfoPromise ??= transport
      .call<unknown>("core_webservice_get_site_info", {})
      .then((d) => parseResponse(RawSiteInfo, d, "core_webservice_get_site_info"))
      .catch((err: unknown) => {
        siteInfoPromise = undefined; // do not cache failures
        throw err;
      });
    return siteInfoPromise;
  };

  async function getUserId(): Promise<number> {
    if (opts.userId !== undefined) return opts.userId;
    return (await loadSiteInfo()).userid;
  }

  function fileUrl(url: string): string {
    return opts.embedFileAuth ? transport.rewriteFileUrl(url) : url;
  }

  function mapModule(
    courseId: string,
    section: { id: string; name: string },
    m: z.infer<typeof RawModule>
  ): LmsModule {
    const type = toModuleType(m.modname);
    const contents = m.contents ?? [];
    const files = contents
      .filter((c) => (c.type === "file" || c.type == null) && c.fileurl)
      .map((c) => ({
        name: c.filename ?? "file",
        url: fileUrl(c.fileurl as string),
        mimeType: emptyToNull(c.mimetype),
        size: c.filesize ?? null,
        modified: isoFromUnix(c.timemodified)
      }));
    // mod_url: the real target is the content entry of type "url"; the module url only redirects to it.
    const externalUrl = type === "url" ? contents.find((c) => c.type === "url")?.fileurl : undefined;
    const state = m.completiondata?.state;
    return {
      id: String(m.id),
      courseId,
      sectionId: section.id,
      sectionName: section.name,
      name: m.name,
      type,
      rawType: m.modname,
      url: emptyToNull(externalUrl) ?? emptyToNull(m.url),
      descriptionHtml: stripPluginfilePlaceholders(m.description),
      files,
      // completion 0 = tracking disabled for the activity
      completed: m.completion === 0 || state === undefined ? null : state !== 0,
      visible: m.uservisible ?? m.visible !== 0
    };
  }

  const adapter: PlatonAdapter = {
    async getSiteInfo() {
      let info: z.infer<typeof RawSiteInfo>;
      try {
        info = await loadSiteInfo();
      } catch (err) {
        // TODO(verify): core_webservice_get_site_info may not be exposed over lib/ajax/service.php (web mode).
        if (opts.userId !== undefined && !(err instanceof AdapterAuthError)) {
          return { userId: opts.userId, fullName: "", siteName: "Platon", release: null };
        }
        throw err;
      }
      return { userId: info.userid, fullName: info.fullname, siteName: info.sitename, release: emptyToNull(info.release) };
    },

    async getCourses() {
      const fn = "core_course_get_enrolled_courses_by_timeline_classification";
      const raw = parseResponse(
        RawEnrolledCourses,
        await transport.call<unknown>(fn, { classification: "all", limit: 0, offset: 0, sort: "fullname" }),
        fn
      );
      return raw.courses.map(
        (c): LmsCourse => ({
          id: String(c.id),
          shortName: c.shortname,
          fullName: c.fullname,
          category: emptyToNull(c.coursecategory),
          progress: c.progress ?? null,
          startDate: isoFromUnix(c.startdate),
          endDate: isoFromUnix(c.enddate),
          imageUrl: emptyToNull(c.courseimage),
          url: courseUrl(c.id),
          // TODO(verify): whether Vistula stores the USOS course code in Moodle's idnumber.
          usosCourseId: emptyToNull(c.idnumber),
          lastAccess: isoFromUnix(c.lastaccess)
        })
      );
    },

    async getCourseContents(courseId) {
      const raw = parseResponse(
        RawContents,
        await transport.call<unknown>("core_course_get_contents", { courseid: Number(courseId) }),
        "core_course_get_contents"
      );
      return raw.map((s, index): LmsSection => {
        const id = String(s.id);
        const name = emptyToNull(s.name) ?? `Sekcja ${s.section ?? index}`;
        return {
          id,
          name,
          summaryHtml: stripPluginfilePlaceholders(s.summary),
          modules: (s.modules ?? []).map((m) => mapModule(courseId, { id, name }, m))
        };
      });
    },

    async getAssignments() {
      const fn = "mod_assign_get_assignments";
      const raw = parseResponse(RawAssignments, await transport.call<unknown>(fn, {}), fn);
      const nowSec = Math.floor(now().getTime() / 1000);

      const items = raw.courses.flatMap((c) => c.assignments.map((a) => ({ course: c, a })));
      // Submission status is one call per assignment: only for the nearest-due ones (past or future).
      const nearest = items
        .filter((i) => (i.a.duedate ?? 0) > 0 && i.a.nosubmissions !== 1)
        .sort((x, y) => Math.abs((x.a.duedate ?? 0) - nowSec) - Math.abs((y.a.duedate ?? 0) - nowSec))
        .slice(0, maxStatusCalls);

      const statuses = new Map<number, { submitted: boolean; graded: boolean; grade: string | null }>();
      await mapLimit(nearest, 5, async ({ a }) => {
        try {
          const s = parseResponse(
            RawSubmissionStatus,
            await transport.call<unknown>("mod_assign_get_submission_status", { assignid: a.id }),
            "mod_assign_get_submission_status"
          );
          const graded = s.lastattempt?.graded === true || (s.feedback?.gradeddate ?? 0) > 0;
          const display = s.feedback?.gradefordisplay ? stripTags(s.feedback.gradefordisplay) : "";
          statuses.set(a.id, {
            submitted: s.lastattempt?.submission?.status === "submitted" || graded,
            graded,
            grade: graded ? dashToNull(display) : null
          });
        } catch {
          // Best-effort: leave as unknown (submitted=false). A dead token would already have failed the main call;
          // `accessexception` here (-> AdapterAuthError) just means status is not readable for this assignment.
        }
      });

      const out = items.map(({ course, a }): LmsAssignment => {
        const st = statuses.get(a.id);
        return {
          id: String(a.id),
          courseId: String(course.id),
          courseName: course.fullname,
          name: a.name,
          dueDate: isoFromUnix(a.duedate),
          cutoffDate: isoFromUnix(a.cutoffdate),
          submitted: st?.submitted ?? false,
          graded: st?.graded ?? false,
          grade: st?.grade ?? null,
          url: `${base}mod/assign/view.php?id=${a.cmid}`
        };
      });
      // Due-date ascending, assignments without a deadline last.
      return out.sort((x, y) => {
        if (x.dueDate === y.dueDate) return 0;
        if (x.dueDate === null) return 1;
        if (y.dueDate === null) return -1;
        return x.dueDate < y.dueDate ? -1 : 1;
      });
    },

    async getUpcomingEvents(days = 14) {
      const fn = "core_calendar_get_action_events_by_timesort";
      const from = Math.floor(now().getTime() / 1000);
      const raw = parseResponse(
        RawEvents,
        await transport.call<unknown>(fn, {
          timesortfrom: from,
          timesortto: from + Math.round(days * 86_400),
          limitnum: 50,
          limittononsuspendedevents: true
        }),
        fn
      );
      return raw.events.map(
        (e): LmsEvent => ({
          id: String(e.id),
          courseId: e.course ? String(e.course.id) : null,
          courseName: emptyToNull(e.course?.fullname),
          name: e.name,
          type: e.eventtype ?? "unknown",
          moduleType: emptyToNull(e.modulename),
          start: new Date(e.timestart * 1000).toISOString(),
          end: e.timeduration && e.timeduration > 0 ? new Date((e.timestart + e.timeduration) * 1000).toISOString() : null,
          url: emptyToNull(e.action?.url) ?? emptyToNull(e.url),
          actionName: emptyToNull(e.action?.name)
        })
      );
    },

    async getGrades(courseId) {
      const fn = "gradereport_user_get_grade_items";
      const userId = await getUserId();
      const raw = parseResponse(
        RawGradeItems,
        await transport.call<unknown>(fn, { courseid: Number(courseId), userid: userId }),
        fn
      );
      return raw.usergrades.flatMap((ug) =>
        ug.gradeitems.map((g): LmsGradeItem => {
          const type = g.itemtype ?? "unknown";
          return {
            id: String(g.id),
            courseId: String(ug.courseid),
            itemName: emptyToNull(g.itemname) ?? (type === "course" ? "Suma końcowa" : type === "category" ? "Kategoria" : "Element oceny"),
            itemType: type,
            gradeFormatted: dashToNull(g.gradeformatted),
            gradeRaw: g.graderaw ?? null,
            gradeMax: g.grademax ?? null,
            percentage: dashToNull(g.percentageformatted),
            feedbackHtml: stripPluginfilePlaceholders(g.feedback),
            dateGraded: isoFromUnix(g.gradedategraded)
          };
        })
      );
    },

    async getAnnouncements(limit = 20) {
      const forums = parseResponse(
        RawForums,
        await transport.call<unknown>("mod_forum_get_forums_by_courses", { courseids: newsCourseIds }),
        "mod_forum_get_forums_by_courses"
      ).filter((f) => f.type === "news");

      const perForum = await mapLimit(forums, 4, async (forum): Promise<Announcement[]> => {
        try {
          const fn = "mod_forum_get_forum_discussions";
          const d = parseResponse(
            RawDiscussions,
            await transport.call<unknown>(fn, { forumid: forum.id, sortorder: -1, page: 0, perpage: limit }),
            fn
          );
          return d.discussions.map((x): Announcement => {
            const discussionId = x.discussion ?? x.id;
            return {
              id: `platon:${discussionId}`,
              source: "platon",
              title: emptyToNull(x.subject) ?? emptyToNull(x.name) ?? "(bez tytułu)",
              bodyHtml: stripPluginfilePlaceholders(x.message) ?? "",
              publishedAt: isoFromUnix(x.created ?? x.timemodified ?? x.modified) ?? new Date(0).toISOString(),
              author: emptyToNull(x.userfullname),
              category: forum.name,
              url: `${base}mod/forum/discuss.php?d=${discussionId}`
            };
          });
        } catch {
          return []; // one inaccessible course forum must not hide the others
        }
      });
      return perForum
        .flat()
        .sort((a, b) => (a.publishedAt < b.publishedAt ? 1 : a.publishedAt > b.publishedAt ? -1 : 0))
        .slice(0, limit);
    },

    async getFileDownload(url) {
      // Never hand credentials to a foreign host.
      if (!isSameOrigin(url, base)) {
        throw new AdapterUnavailableError("Refusing to download a file from a different origin than Platon");
      }
      return { url: transport.rewriteFileUrl(url), headers: transport.fileHeaders() };
    }
  };
  return adapter;
}
