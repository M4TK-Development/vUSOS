import type { Grade } from "@vusos/shared";
import { average, gradeNumber } from "./format";

export function computeAverage(grades: Grade[]): number | null {
  const nums: number[] = [];
  for (const g of grades) {
    if (!g.countsIntoAverage) continue;
    const n = gradeNumber(g.valueSymbol);
    if (n != null) nums.push(n);
  }
  return average(nums);
}

export interface CourseGroup {
  courseId: string;
  name: string;
  grades: Grade[];
}

export function groupByCourse(grades: Grade[], nameOf: (g: Grade) => string): CourseGroup[] {
  const map = new Map<string, CourseGroup>();
  for (const g of grades) {
    const cur = map.get(g.courseId) ?? { courseId: g.courseId, name: nameOf(g), grades: [] };
    cur.grades.push(g);
    map.set(g.courseId, cur);
  }
  return [...map.values()].sort((a, b) => a.name.localeCompare(b.name, "pl"));
}
