import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import "./index.css";
import App from "./App";
import { AuthProvider } from "./features/auth/AuthContext";
import { SolveRunnerProvider } from "./features/admin/solve/SolveRunnerProvider";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <SolveRunnerProvider>
          <App />
        </SolveRunnerProvider>
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>,
);
