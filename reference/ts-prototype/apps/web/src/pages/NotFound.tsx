import { Link } from "react-router-dom";
import { EmptyState } from "../components/EmptyState";

export function NotFound() {
  return (
    <div className="card">
      <EmptyState title="Nie znaleziono strony" hint="Adres, który otworzyłeś, nie istnieje." />
      <div className="text-center">
        <Link to="/" className="btn no-underline">Wróć na start</Link>
      </div>
    </div>
  );
}
