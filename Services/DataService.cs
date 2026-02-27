using Microsoft.Data.Sqlite;
using GantsPlace.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GantsPlace.Services
{
    public static class DataService
    {
        // Chemin de la base de donnees - utilise le repertoire de l'application
        private static readonly string connectionString = $"Data Source=ReservationSalle.db;";

        // Propriete statique pour stocker les salles en cache
        public static List<Salle> Salles { get; private set; } = new();

        // Charger les salles depuis la base de donnees
        public static void LoadSalles()
        {
            Salles = GetSalles();
            InitializeHistoriqueTable();
        }

        // CREER LA TABLE HISTORIQUE SI ELLE N'EXISTE PAS OU LA RECREER SI SCHEMA INCORRECT
        private static void InitializeHistoriqueTable()
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            // Verifier si la table existe
            string checkTableQuery = "SELECT name FROM sqlite_master WHERE type='table' AND name='Historique';";
            using var checkCmd = new SqliteCommand(checkTableQuery, conn);
            var tableExists = checkCmd.ExecuteScalar() != null;

            if (tableExists)
            {
                // Verifier le schema
                string schemaQuery = "PRAGMA table_info(Historique);";
                using var schemaCmd = new SqliteCommand(schemaQuery, conn);
                using var reader = schemaCmd.ExecuteReader();

                var columns = new List<string>();
                while (reader.Read())
                {
                    columns.Add(reader.GetString(1)); // name column
                }

                var requiredColumns = new[] { "id_historique", "id_user", "id_reservation", "action", "timestamp" };
                bool schemaCorrect = requiredColumns.All(col => columns.Contains(col));

                if (!schemaCorrect)
                {
                    // Supprimer et recreer
                    string dropQuery = "DROP TABLE Historique;";
                    using var dropCmd = new SqliteCommand(dropQuery, conn);
                    dropCmd.ExecuteNonQuery();
                    tableExists = false;
                }
            }

            if (!tableExists)
            {
                string createQuery = @"
                    CREATE TABLE Historique (
                        id_historique INTEGER PRIMARY KEY AUTOINCREMENT,
                        id_user INTEGER NOT NULL,
                        id_reservation INTEGER NOT NULL,
                        action TEXT NOT NULL,
                        timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                        FOREIGN KEY (id_user) REFERENCES User(id_user),
                        FOREIGN KEY (id_reservation) REFERENCES Reservation(id_reservation)
                    )";

                using var createCmd = new SqliteCommand(createQuery, conn);
                createCmd.ExecuteNonQuery();
            }
        }

        // AUTHENTIFICATION
        public static bool Authentifier(string email, string mdp)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string query = "SELECT id_user, nom_user, email FROM User WHERE email=@email AND motdepasse=@mdp";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@email", email);
            cmd.Parameters.AddWithValue("@mdp", mdp);

            using var reader = cmd.ExecuteReader();

            if (reader.Read())
            {
                Session.UtilisateurConnecte = new Utilisateur
                {
                    Id = reader.GetInt32(0),
                    NomComplet = reader.GetString(1),
                    Email = reader.GetString(2)
                };

                LoadUserReservations(Session.UtilisateurConnecte);

                return true;
            }

            return false;
        }

        // INSCRIPTION
        public static bool Inscrire(string nom, string email, string mdp)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string checkQuery = "SELECT COUNT(*) FROM User WHERE email=@email";
            using var checkCmd = new SqliteCommand(checkQuery, conn);
            checkCmd.Parameters.AddWithValue("@email", email);
            int count = Convert.ToInt32(checkCmd.ExecuteScalar());

            if (count > 0)
                return false;

            string query = "INSERT INTO User(nom_user,email,motdepasse) VALUES(@nom,@email,@mdp)";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@nom", nom);
            cmd.Parameters.AddWithValue("@email", email);
            cmd.Parameters.AddWithValue("@mdp", mdp);

            cmd.ExecuteNonQuery();
            return true;
        }

        // CREAU
        public static int GetOrCreateCreneau(string heureDebut, string heureFin)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string checkQuery = "SELECT id_creneau FROM Creneau WHERE heure_debut=@debut AND heure_fin=@fin";
            using var checkCmd = new SqliteCommand(checkQuery, conn);
            checkCmd.Parameters.AddWithValue("@debut", heureDebut);
            checkCmd.Parameters.AddWithValue("@fin", heureFin);

            var result = checkCmd.ExecuteScalar();
            if (result != null)
                return Convert.ToInt32(result);

            string insertQuery = "INSERT INTO Creneau(heure_debut, heure_fin) VALUES(@debut, @fin); SELECT last_insert_rowid();";
            using var insertCmd = new SqliteCommand(insertQuery, conn);
            insertCmd.Parameters.AddWithValue("@debut", heureDebut);
            insertCmd.Parameters.AddWithValue("@fin", heureFin);

            return Convert.ToInt32(insertCmd.ExecuteScalar());
        }

        // AJOUT RESERVATION
        public static void AjouterReservation(int salleId, int creneauId, string jour)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string query = @"INSERT INTO Reservation
                            (id_salle,id_user,id_creneau,jour,statut,nom_salle)
                            VALUES(@salle,@user,@creneau,@jour,@statut,
                                   (SELECT nom_salle FROM Salle WHERE id_salle=@salle)); SELECT last_insert_rowid();";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@salle", salleId);
            cmd.Parameters.AddWithValue("@user", Session.UtilisateurConnecte!.Id);
            cmd.Parameters.AddWithValue("@creneau", creneauId);
            cmd.Parameters.AddWithValue("@jour", jour);
            cmd.Parameters.AddWithValue("@statut", "Confirmée");

            int reservationId = Convert.ToInt32(cmd.ExecuteScalar());

            // Inserer dans Historique
            string historiqueQuery = @"INSERT INTO Historique (id_user, id_reservation, action) VALUES(@user, @reservation, 'created')";
            using var histCmd = new SqliteCommand(historiqueQuery, conn);
            histCmd.Parameters.AddWithValue("@user", Session.UtilisateurConnecte!.Id);
            histCmd.Parameters.AddWithValue("@reservation", reservationId);
            histCmd.ExecuteNonQuery();
        }

        // CHARGER LES RESERVATIONS D'UN UTILISATEUR
        public static void LoadUserReservations(Utilisateur utilisateur)
        {
            utilisateur.Reservations.Clear();

            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string query = @"
                SELECT r.id_reservation, r.id_salle, r.nom_salle, r.jour, 
                       r.statut, c.heure_debut, c.heure_fin, s.type_salle,
                       u.nom_user, u.email
                FROM Reservation r
                JOIN Creneau c ON r.id_creneau = c.id_creneau
                JOIN Salle s ON r.id_salle = s.id_salle
                JOIN User u ON r.id_user = u.id_user
                WHERE r.id_user = @userId
                ORDER BY r.jour DESC, c.heure_debut DESC";

            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@userId", utilisateur.Id);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                utilisateur.Reservations.Add(new Reservation
                {
                    Id = reader.GetInt32(0),
                    SalleId = reader.GetInt32(1),
                    SalleNom = reader.GetString(2),
                    Date = DateTime.Parse(reader.GetString(3)),
                    Statut = reader.GetString(4),
                    HeureDebut = TimeSpan.Parse(reader.GetString(5)),
                    HeureFin = TimeSpan.Parse(reader.GetString(6)),
                    TypeSalle = reader.GetString(7),
                    UserNom = reader.GetString(8),
                    UserEmail = reader.GetString(9)
                });
            }
        }

        // ANNULER UNE RESERVATION
        public static void AnnulerReservation(Reservation reservation)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string query = "UPDATE Reservation SET statut='Annulee' WHERE id_reservation=@id";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", reservation.Id);
            cmd.ExecuteNonQuery();

            // Inserer dans Historique
            string historiqueQuery = @"INSERT INTO Historique (id_user, id_reservation, action) VALUES(@user, @reservation, 'cancelled')";
            using var histCmd = new SqliteCommand(historiqueQuery, conn);
            histCmd.Parameters.AddWithValue("@user", Session.UtilisateurConnecte!.Id);
            histCmd.Parameters.AddWithValue("@reservation", reservation.Id);
            histCmd.ExecuteNonQuery();

            if (Session.UtilisateurConnecte != null)
            {
                var res = Session.UtilisateurConnecte.Reservations.FirstOrDefault(r => r.Id == reservation.Id);
                if (res != null)
                    res.Statut = "Annulee";
            }
        }

        // RECUPERER INFO DE SALLES DE LA BD
        public static List<Salle> GetSalles()
        {
            var salles = new List<Salle>();

            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string query = @"SELECT s.id_salle, s.nom_salle, s.capacite_salle, s.type_salle,s.description
                             FROM Salle s";

            using var cmd = new SqliteCommand(query, conn);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var salle = new Salle
                {
                    Id = reader.GetInt32(0),
                    Nom = reader.GetString(1),
                    Capacite = reader.GetInt32(2),
                    TypeSalle = reader.GetString(3),
                    Description= reader.GetString(4),
                    Equipements = GetEquipementsSalle(reader.GetInt32(0)),
                    ImagePath = GetFirstImageForSalle(reader.GetInt32(0))
                };

                salles.Add(salle);
            }

            return salles;
        }
         //RECUPER  IMAGE DE LA SALLE DANS LA BD
        private static string? GetFirstImageForSalle(int salleId)
        {
            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string query = "SELECT image FROM Salle WHERE id_salle=@id;";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", salleId);

            var result = cmd.ExecuteScalar();
            return result?.ToString();
        }

        //RECUPER  Equipement DE LA SALLE DANS LA BD
        private static List<string> GetEquipementsSalle(int idSalle)
        {
            var equipements = new List<string>();

            using var conn = new SqliteConnection(connectionString);
            conn.Open();

            string query = @"SELECT nom_equipement FROM Equipement WHERE id_salle=@id";
            using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@id", idSalle);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                equipements.Add(reader.GetString(0));

            return equipements;
        }
    }
}
