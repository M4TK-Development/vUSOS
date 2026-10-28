import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { UseQueryResult } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import { ApiError } from "../api/client";
import { ConnectPrompt } from "../components/ConnectPrompt";
import { ConnectionBanners } from "../components/ConnectionBanners";
import { QueryBoundary } from "../components/QueryBoundary";
import { SafeHtml } from "../components/SafeHtml";
import { AssignmentBadges } from "../pages/CourseDetail";
import { buildLaunchUrl } from "../pages/Settings";

const wrap = (ui: React.ReactElement) => render(<MemoryRouter>{ui}</MemoryRouter>);
const fakeQuery = (partial: Record<string, unknown>) => partial as unknown as UseQueryResult<string>;

describe("ConnectPrompt", () => {
  it("prompts to connect and links to settings", () => {
    wrap(<ConnectPrompt system="usos" />);
    expect(screen.getByText("System USOS nie jest połączony")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Połącz z USOS" })).toHaveAttribute("href", "/settings");
  });

  it("shows reconnect wording when expired", () => {
    wrap(<ConnectPrompt system="platon" expired />);
    expect(screen.getByText("Sesja w systemie Platon wygasła")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Połącz ponownie" })).toBeInTheDocument();
  });
});

describe("QueryBoundary", () => {
  it("renders ConnectPrompt for 409 NOT_CONNECTED instead of crashing", () => {
    const err = new ApiError(409, "nope", "NOT_CONNECTED", "platon");
    wrap(<QueryBoundary query={fakeQuery({ isPending: false, isError: true, error: err })}>{() => <div>data</div>}</QueryBoundary>);
    expect(screen.getByTestId("connect-prompt")).toHaveTextContent("Platon");
    expect(screen.queryByText("data")).not.toBeInTheDocument();
  });

  it("renders expired prompt for 424 ADAPTER_AUTH, using the fallback system", () => {
    const err = new ApiError(424, "expired", "ADAPTER_AUTH");
    wrap(<QueryBoundary system="usos" query={fakeQuery({ isPending: false, isError: true, error: err })}>{() => <div>data</div>}</QueryBoundary>);
    expect(screen.getByTestId("connect-prompt")).toHaveTextContent("Sesja w systemie USOS wygasła");
  });

  it("renders generic error with retry for other errors", () => {
    wrap(<QueryBoundary query={fakeQuery({ isPending: false, isError: true, error: new ApiError(500, "boom"), refetch: () => undefined })}>{() => <div />}</QueryBoundary>);
    expect(screen.getByText("Nie udało się wczytać danych")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Spróbuj ponownie/ })).toBeInTheDocument();
  });

  it("renders data and the empty state", () => {
    const { rerender } = wrap(<QueryBoundary query={fakeQuery({ isPending: false, isError: false, data: "hello" })}>{(d) => <div>{d}</div>}</QueryBoundary>);
    expect(screen.getByText("hello")).toBeInTheDocument();
    rerender(
      <MemoryRouter>
        <QueryBoundary query={fakeQuery({ isPending: false, isError: false, data: "" })} isEmpty={(d) => d === ""} empty={<p>pusto</p>}>
          {(d) => <div>{d}</div>}
        </QueryBoundary>
      </MemoryRouter>,
    );
    expect(screen.getByText("pusto")).toBeInTheDocument();
  });
});

describe("SafeHtml", () => {
  it("strips scripts and handlers, and opens links safely", () => {
    const { container } = render(
      <SafeHtml html={'<p onclick="x()">Cześć</p><script>alert(1)</script><a href="https://a.pl">link</a><img src=x onerror="y()">'} />,
    );
    expect(container.querySelector("script")).toBeNull();
    expect(container.innerHTML).not.toContain("onclick");
    expect(container.innerHTML).not.toContain("onerror");
    const a = container.querySelector("a");
    expect(a).toHaveAttribute("target", "_blank");
    expect(a).toHaveAttribute("rel", "noopener noreferrer");
  });
});

describe("ConnectionBanners", () => {
  it("shows banners only for non-connected systems", () => {
    wrap(<ConnectionBanners connections={{ usos: "connected", platon: "expired" }} />);
    expect(screen.queryByText(/USOS/)).not.toBeInTheDocument();
    expect(screen.getByText("Połączenie z systemem Platon wygasło.")).toBeInTheDocument();
  });
});

describe("AssignmentBadges", () => {
  it("shows submitted and not-submitted states", () => {
    const { rerender } = render(<AssignmentBadges a={{ dueDate: null, submitted: true, graded: false, grade: null }} />);
    expect(screen.getByText("oddane")).toBeInTheDocument();
    rerender(<AssignmentBadges a={{ dueDate: "2020-01-01T10:00:00Z", submitted: false, graded: false, grade: null }} />);
    expect(screen.getByText("nie oddane")).toBeInTheDocument();
    expect(screen.getByText(/termin:/)).toHaveClass("badge-red");
  });
});

describe("Platon launch URL", () => {
  it("builds the mobile launch link", () => {
    expect(buildLaunchUrl("https://platon.vistula.edu.pl", "123", "moodlemobile")).toBe(
      "https://platon.vistula.edu.pl/admin/tool/mobile/launch.php?service=moodle_mobile_app&passport=123&urlscheme=moodlemobile",
    );
  });
});
