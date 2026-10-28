import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "react-router-dom";
import { createQueryClient } from "./api/queryClient";
import { installDeepLinks } from "./deeplink";
import { router } from "./router";
import { Toaster } from "./toast";
import "./index.css";

const queryClient = createQueryClient(() => {
  if (router.state.location.pathname !== "/login") {
    queryClient.removeQueries({ queryKey: ["me"] });
    void router.navigate("/login", { replace: true });
  }
});

installDeepLinks({ queryClient, navigate: (to) => void router.navigate(to) });

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
      <Toaster />
    </QueryClientProvider>
  </StrictMode>,
);
