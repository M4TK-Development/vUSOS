import { createBrowserRouter, createHashRouter } from "react-router-dom";
import { AuthGate } from "./components/AuthGate";
import { Layout } from "./components/Layout";
import { Announcements } from "./pages/Announcements";
import { CourseDetail } from "./pages/CourseDetail";
import { Courses } from "./pages/Courses";
import { Dashboard } from "./pages/Dashboard";
import { Grades } from "./pages/Grades";
import { Login } from "./pages/Login";
import { Messages } from "./pages/Messages";
import { NotFound } from "./pages/NotFound";
import { Payments } from "./pages/Payments";
import { Plan } from "./pages/Plan";
import { SettingsPage } from "./pages/Settings";

export const routes = [
  { path: "/login", element: <Login /> },
  {
    element: <AuthGate />,
    children: [
      {
        element: <Layout />,
        children: [
          { path: "/", element: <Dashboard /> },
          { path: "/plan", element: <Plan /> },
          { path: "/oceny", element: <Grades /> },
          { path: "/platnosci", element: <Payments /> },
          { path: "/wiadomosci", element: <Messages /> },
          { path: "/kursy", element: <Courses /> },
          { path: "/kursy/:id", element: <CourseDetail /> },
          { path: "/ogloszenia", element: <Announcements /> },
          { path: "/settings", element: <SettingsPage /> },
          { path: "*", element: <NotFound /> },
        ],
      },
    ],
  },
];

// file:// (Capacitor/Tauri bundles served without a server) has no usable pathname -> hash routing.
const useHash = typeof window !== "undefined" && window.location.protocol === "file:";
export const router = useHash ? createHashRouter(routes) : createBrowserRouter(routes);
