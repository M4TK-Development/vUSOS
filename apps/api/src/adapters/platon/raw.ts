import { z } from "zod";

/**
 * Lenient schemas for the parts of Moodle web service responses we consume (Moodle 4.x).
 * Everything not essential is nullish: Moodle versions/plugins differ in what they return.
 */

export const RawSiteInfo = z.object({
  userid: z.number(),
  fullname: z.string(),
  sitename: z.string(),
  release: z.string().nullish()
});

export const RawCourse = z.object({
  id: z.number(),
  fullname: z.string(),
  shortname: z.string(),
  idnumber: z.string().nullish(),
  startdate: z.number().nullish(),
  enddate: z.number().nullish(),
  courseimage: z.string().nullish(),
  progress: z.number().nullish(),
  coursecategory: z.string().nullish(),
  // TODO(verify): not part of the timeline-classification response; present in core_enrol_get_users_courses.
  lastaccess: z.number().nullish()
});
export const RawEnrolledCourses = z.object({ courses: z.array(RawCourse), nextoffset: z.number().nullish() });

export const RawContentFile = z.object({
  type: z.string().nullish(),
  filename: z.string().nullish(),
  filesize: z.number().nullish(),
  fileurl: z.string().nullish(),
  timemodified: z.number().nullish(),
  mimetype: z.string().nullish()
});

export const RawModule = z.object({
  id: z.number(),
  url: z.string().nullish(),
  name: z.string(),
  description: z.string().nullish(),
  visible: z.number().nullish(),
  uservisible: z.boolean().nullish(),
  modname: z.string(),
  completion: z.number().nullish(),
  completiondata: z.object({ state: z.number() }).nullish(),
  contents: z.array(RawContentFile).nullish()
});

export const RawSection = z.object({
  id: z.number(),
  name: z.string().nullish(),
  summary: z.string().nullish(),
  section: z.number().nullish(),
  modules: z.array(RawModule).nullish()
});
export const RawContents = z.array(RawSection);

export const RawAssignment = z.object({
  id: z.number(),
  cmid: z.number(),
  name: z.string(),
  nosubmissions: z.number().nullish(),
  duedate: z.number().nullish(),
  cutoffdate: z.number().nullish()
});
export const RawAssignments = z.object({
  courses: z.array(
    z.object({
      id: z.number(),
      fullname: z.string(),
      assignments: z.array(RawAssignment)
    })
  )
});

export const RawSubmissionStatus = z.object({
  lastattempt: z
    .object({
      submission: z.object({ status: z.string().nullish() }).nullish(),
      graded: z.boolean().nullish()
    })
    .nullish(),
  feedback: z
    .object({
      gradefordisplay: z.string().nullish(),
      gradeddate: z.number().nullish()
    })
    .nullish()
});

export const RawEvent = z.object({
  id: z.number(),
  name: z.string(),
  modulename: z.string().nullish(),
  eventtype: z.string().nullish(),
  timestart: z.number(),
  timeduration: z.number().nullish(),
  url: z.string().nullish(),
  course: z.object({ id: z.number(), fullname: z.string().nullish(), shortname: z.string().nullish() }).nullish(),
  action: z.object({ name: z.string().nullish(), url: z.string().nullish() }).nullish()
});
export const RawEvents = z.object({ events: z.array(RawEvent) });

export const RawGradeItem = z.object({
  id: z.number(),
  itemname: z.string().nullish(),
  itemtype: z.string().nullish(),
  graderaw: z.number().nullish(),
  gradedategraded: z.number().nullish(),
  gradeformatted: z.string().nullish(),
  grademax: z.number().nullish(),
  percentageformatted: z.string().nullish(),
  feedback: z.string().nullish()
});
export const RawGradeItems = z.object({
  usergrades: z.array(z.object({ courseid: z.number(), gradeitems: z.array(RawGradeItem) }))
});

export const RawForums = z.array(
  z.object({
    id: z.number(),
    course: z.number(),
    type: z.string(),
    name: z.string()
  })
);

export const RawDiscussion = z.object({
  id: z.number(),
  discussion: z.number().nullish(),
  name: z.string().nullish(),
  subject: z.string().nullish(),
  message: z.string().nullish(),
  created: z.number().nullish(),
  modified: z.number().nullish(),
  timemodified: z.number().nullish(),
  userfullname: z.string().nullish()
});
export const RawDiscussions = z.object({ discussions: z.array(RawDiscussion) });
