import { Routes, Route } from "react-router-dom";
import { RegisterPage } from "./features/auth/pages/RegisterPage";

export default function App() {
  return (
    <Routes>
      <Route path="" element={<RegisterPage />} />
    </Routes>
  );
}
