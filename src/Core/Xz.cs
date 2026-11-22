namespace Autoexec.Core;

/// <summary>Déplie un fichier .xz au fil de la lecture : c'est le format des images de Raspberry Pi OS, que ni Windows ni
/// .NET ne savent ouvrir. Écrit d'après les spécifications publiques des formats xz et LZMA (domaine public), sans
/// bibliothèque à télécharger. Ne vérifie pas les contrôles internes du fichier : celui qui s'en sert compare l'empreinte
/// SHA-256 de ce qui en sort à celle que l'éditeur publie, ce qui est plus fort.</summary>
sealed class Xz(Stream source) : Stream
{
    readonly byte[] _entree = new byte[1 << 16], _morceau = new byte[1 << 16];
    int _entreePos, _entreeFin;
    long _lus;                      // octets du fichier déjà consommés
    int _controle = -1;             // taille du contrôle qui suit chaque bloc ; -1 tant que l'en-tête n'est pas lu
    bool _dansBloc, _fini;
    long _debutDonnees;

    // fenêtre (« dictionnaire ») où se déplient les données, et ce qui reste à rendre
    byte[] _dico = [];
    int _pos, _aRendre;
    long _total;                    // octets dépliés depuis la dernière remise à zéro de la fenêtre
    bool _dicoNeuf = true, _etatNeuf = true, _reglagesConnus;

    // état du décodeur LZMA
    int _lc, _lp, _pb, _etat;
    uint _rep0, _rep1, _rep2, _rep3, _plage, _code;
    int _mp, _mFin;                 // position et fin dans _morceau
    ushort[] _litteraux = [];
    readonly ushort[] _estCopie = new ushort[12 << 4], _estRep = new ushort[12], _estRepG0 = new ushort[12], _estRepG1 = new ushort[12],
        _estRepG2 = new ushort[12], _estRep0Long = new ushort[12 << 4], _creneau = new ushort[4 << 6], _distances = new ushort[115], _alignement = new ushort[16];
    readonly Longueur _longueur = new(), _longueurRep = new();

    sealed class Longueur
    {
        public ushort Choix, Choix2;
        public readonly ushort[] Bas = new ushort[16 << 3], Milieu = new ushort[16 << 3], Haut = new ushort[256];
        public void Remettre() { Choix = Choix2 = 1024; Array.Fill(Bas, (ushort)1024); Array.Fill(Milieu, (ushort)1024); Array.Fill(Haut, (ushort)1024); }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }

    static InvalidDataException Abime(string quoi) => new("fichier .xz abîmé ou non géré : " + quoi);

    public override int Read(byte[] buffer, int offset, int count)
    {
        int rendu = 0;
        while (rendu < count)
        {
            if (_aRendre == 0 && !Suivant()) break;
            // ce qui vient d'être déplié est à la fin de la fenêtre, juste avant _pos (elle tourne en rond)
            int debut = _pos - _aRendre;
            if (debut < 0) debut += _dico.Length;
            int n = Math.Min(Math.Min(count - rendu, _aRendre), _dico.Length - debut);
            Buffer.BlockCopy(_dico, debut, buffer, offset + rendu, n);
            rendu += n;
            _aRendre -= n;
        }
        return rendu;
    }

    // ----- lecture du fichier -----

    /// <summary>Vrai quand le fichier n'a plus rien à donner.</summary>
    bool Epuise()
    {
        if (_entreePos < _entreeFin) return false;
        _entreeFin = source.Read(_entree, 0, _entree.Length);
        _entreePos = 0;
        return _entreeFin <= 0;
    }

    int Octet()
    {
        if (Epuise()) throw Abime("fin inattendue");
        _lus++;
        return _entree[_entreePos++];
    }

    /// <summary>Un entier de longueur variable (7 bits par octet), comme dans l'index.</summary>
    ulong Entier()
    {
        ulong v = 0;
        for (int decalage = 0; decalage < 63; decalage += 7)
        {
            int b = Octet();
            v |= (ulong)(b & 0x7F) << decalage;
            if (b < 0x80) return v;
        }
        throw Abime("entier trop long");
    }

    /// <summary>Après le dernier bloc : l'index et le pied du flux, qu'on saute. Un fichier peut être fait de plusieurs flux
    /// mis bout à bout, séparés ou non par des zéros : s'il reste autre chose, c'est le flux suivant.</summary>
    void FinDeFlux()
    {
        long debut = _lus - 1; // l'index commence à l'octet nul déjà lu
        for (ulong blocs = Entier(); blocs > 0; blocs--) { Entier(); Entier(); }
        while ((_lus - debut) % 4 != 0) Octet();
        for (int i = 0; i < 4 + 12; i++) Octet(); // contrôle de l'index, puis pied du flux
        while (true)
        {
            if (Epuise()) { _fini = true; return; }
            if (_entree[_entreePos] != 0) break;
            Octet();
        }
        _controle = -1; // un autre flux commence : son en-tête sera lu
    }

    void Lire(byte[] vers, int n) { for (int i = 0; i < n; i++) vers[i] = (byte)Octet(); }

    /// <summary>Prépare le morceau suivant dans la fenêtre. Faux à la fin du fichier.</summary>
    bool Suivant()
    {
        while (!_fini)
        {
            if (_controle < 0) EnTete();
            if (!_dansBloc)
            {
                int taille = Octet();
                if (taille == 0) { FinDeFlux(); continue; }     // l'index : il n'y a plus de bloc dans ce flux
                Bloc((taille + 1) * 4 - 1);
                continue;
            }
            int commande = Octet();
            if (commande == 0)
            {
                // fin du bloc : remplissage jusqu'à un multiple de 4, puis le contrôle, que l'on saute
                for (long reste = (4 - (_lus - _debutDonnees) % 4) % 4; reste > 0; reste--) Octet();
                for (int i = 0; i < _controle; i++) Octet();
                _dansBloc = false;
                continue;
            }
            if (commande >= 0x80)
            {
                int deplie = ((commande & 0x1F) << 16) + (Octet() << 8) + Octet() + 1, serre = (Octet() << 8) + Octet() + 1, remise = (commande >> 5) & 3;
                if (remise == 3) { _dicoNeuf = false; _pos = 0; _total = 0; }
                else if (_dicoNeuf) throw Abime("premier morceau sans remise à zéro");
                if (remise >= 2) Reglages(Octet());
                else if (!_reglagesConnus) throw Abime("réglages absents");
                if (remise >= 1) _etatNeuf = true;
                Lire(_morceau, serre);
                _mp = 0; _mFin = serre;
                Deplier(deplie);
                _aRendre = deplie;
                return true;
            }
            if (commande > 2) throw Abime("commande inconnue");
            // morceau stocké tel quel
            int n = (Octet() << 8) + Octet() + 1;
            if (commande == 1) { _dicoNeuf = false; _pos = 0; _total = 0; }
            else if (_dicoNeuf) throw Abime("premier morceau sans remise à zéro");
            for (int i = 0; i < n; i++) Poser((byte)Octet());
            _aRendre = n;
            return true;
        }
        return false;
    }

    void EnTete()
    {
        var tete = new byte[12];
        Lire(tete, 12);
        if (tete[0] != 0xFD || tete[1] != '7' || tete[2] != 'z' || tete[3] != 'X' || tete[4] != 'Z' || tete[5] != 0 || tete[6] != 0)
            throw Abime("ce n'est pas un fichier .xz");
        // taille du contrôle selon son genre : aucun, CRC32, CRC64, SHA-256…
        int genre = tete[7] & 0x0F;
        _controle = genre == 0 ? 0 : genre < 4 ? 4 : genre < 7 ? 8 : genre < 10 ? 16 : genre < 13 ? 32 : 64;
    }

    void Bloc(int reste)
    {
        var tete = new byte[reste];
        Lire(tete, reste);
        int p = 1;
        ulong Nombre()
        {
            ulong v = 0;
            for (int decalage = 0; ; decalage += 7)
            {
                if (p >= tete.Length - 4 || decalage > 56) throw Abime("en-tête de bloc");
                byte b = tete[p++];
                v |= (ulong)(b & 0x7F) << decalage;
                if (b < 0x80) return v;
            }
        }
        if ((tete[0] & 0x03) != 0 || (tete[0] & 0x3C) != 0) throw Abime("plusieurs filtres");
        if ((tete[0] & 0x40) != 0) Nombre();
        if ((tete[0] & 0x80) != 0) Nombre();
        if (Nombre() != 0x21 || Nombre() != 1) throw Abime("filtre autre que LZMA2");
        int bits = tete[p];
        if (bits > 38) throw Abime("fenêtre trop grande");
        long fenetre = (long)(2 | (bits & 1)) << (bits / 2 + 11);
        if (fenetre > 1L << 29) throw Abime("fenêtre trop grande");
        // jamais moins de 2 Mo : un morceau déplié (2 Mo au plus) doit y tenir en entier le temps d'être rendu
        fenetre = Math.Max(fenetre, 1L << 21);
        if (_dico.Length != fenetre) _dico = new byte[fenetre];
        _dansBloc = true;
        _dicoNeuf = true;
        _debutDonnees = _lus;
    }

    void Reglages(int octet)
    {
        if (octet >= 225) throw Abime("réglages");
        _lc = octet % 9;
        _lp = octet / 9 % 5;
        _pb = octet / 45;
        if (_lc + _lp > 4) throw Abime("réglages");
        _litteraux = new ushort[0x300 << (_lc + _lp)];
        _reglagesConnus = true;
    }

    // ----- LZMA -----

    void Poser(byte octet)
    {
        _dico[_pos] = octet;
        if (++_pos == _dico.Length) _pos = 0;
        _total++;
    }

    byte Avant(uint distance)
    {
        int i = _pos - (int)distance - 1;
        return _dico[i < 0 ? i + _dico.Length : i];
    }

    void Normaliser()
    {
        if (_plage >= 1u << 24) return;
        if (_mp >= _mFin) throw Abime("morceau trop court");
        _plage <<= 8;
        _code = (_code << 8) | _morceau[_mp++];
    }

    uint Bit(ref ushort proba)
    {
        uint borne = (_plage >> 11) * proba, bit;
        if (_code < borne) { proba += (ushort)((2048 - proba) >> 5); _plage = borne; bit = 0; }
        else { proba -= (ushort)(proba >> 5); _code -= borne; _plage -= borne; bit = 1; }
        Normaliser();
        return bit;
    }

    uint Directs(int n)
    {
        uint valeur = 0;
        for (; n > 0; n--)
        {
            _plage >>= 1;
            _code -= _plage;
            uint t = 0 - (_code >> 31);
            _code += _plage & t;
            Normaliser();
            valeur = (valeur << 1) + t + 1;
        }
        return valeur;
    }

    uint Arbre(ushort[] probas, int debut, int bits)
    {
        uint m = 1;
        for (int i = 0; i < bits; i++) m = (m << 1) + Bit(ref probas[debut + m]);
        return m - (1u << bits);
    }

    uint ArbreInverse(ushort[] probas, int debut, int bits)
    {
        uint m = 1, valeur = 0;
        for (int i = 0; i < bits; i++)
        {
            uint bit = Bit(ref probas[debut + m]);
            m = (m << 1) + bit;
            valeur |= bit << i;
        }
        return valeur;
    }

    uint LireLongueur(Longueur l, int posEtat)
    {
        if (Bit(ref l.Choix) == 0) return Arbre(l.Bas, posEtat << 3, 3);
        if (Bit(ref l.Choix2) == 0) return 8 + Arbre(l.Milieu, posEtat << 3, 3);
        return 16 + Arbre(l.Haut, 0, 8);
    }

    void Deplier(int reste)
    {
        if (_etatNeuf)
        {
            foreach (var t in new[] { _litteraux, _estCopie, _estRep, _estRepG0, _estRepG1, _estRepG2, _estRep0Long, _creneau, _distances, _alignement })
                Array.Fill(t, (ushort)1024);
            _longueur.Remettre();
            _longueurRep.Remettre();
            _etat = 0;
            _rep0 = _rep1 = _rep2 = _rep3 = 0;
            _etatNeuf = false;
        }
        if (_mFin < 5 || _morceau[0] != 0) throw Abime("début de morceau");
        _code = (uint)(_morceau[1] << 24 | _morceau[2] << 16 | _morceau[3] << 8 | _morceau[4]);
        _plage = 0xFFFFFFFF;
        _mp = 5;
        int masquePb = (1 << _pb) - 1, masqueLp = (1 << _lp) - 1;
        while (reste > 0)
        {
            int posEtat = (int)_total & masquePb;
            if (Bit(ref _estCopie[(_etat << 4) + posEtat]) == 0)
            {
                // un octet nouveau, deviné d'après le précédent (et d'après l'octet attendu juste après une copie)
                int debut = 0x300 * ((((int)_total & masqueLp) << _lc) + ((_total == 0 ? 0 : Avant(0)) >> (8 - _lc)));
                uint symbole = 1;
                if (_etat >= 7)
                {
                    uint attendu = Avant(_rep0);
                    do
                    {
                        uint bitAttendu = (attendu >> 7) & 1;
                        attendu <<= 1;
                        uint bit = Bit(ref _litteraux[debut + (int)(((1 + bitAttendu) << 8) + symbole)]);
                        symbole = (symbole << 1) | bit;
                        if (bitAttendu != bit) break;
                    } while (symbole < 0x100);
                }
                while (symbole < 0x100) symbole = (symbole << 1) | Bit(ref _litteraux[debut + (int)symbole]);
                Poser((byte)symbole);
                _etat = _etat < 4 ? 0 : _etat < 10 ? _etat - 3 : _etat - 6;
                reste--;
                continue;
            }
            uint longueur;
            if (Bit(ref _estRep[_etat]) != 0)
            {
                // copie depuis une des quatre dernières distances
                if (_total == 0) throw Abime("copie avant tout octet");
                if (Bit(ref _estRepG0[_etat]) == 0)
                {
                    if (Bit(ref _estRep0Long[(_etat << 4) + posEtat]) == 0)
                    {
                        _etat = _etat < 7 ? 9 : 11;
                        Poser(Avant(_rep0));
                        reste--;
                        continue;
                    }
                }
                else
                {
                    uint distance;
                    if (Bit(ref _estRepG1[_etat]) == 0) distance = _rep1;
                    else
                    {
                        if (Bit(ref _estRepG2[_etat]) == 0) distance = _rep2;
                        else { distance = _rep3; _rep3 = _rep2; }
                        _rep2 = _rep1;
                    }
                    _rep1 = _rep0;
                    _rep0 = distance;
                }
                longueur = LireLongueur(_longueurRep, posEtat);
                _etat = _etat < 7 ? 8 : 11;
            }
            else
            {
                // copie depuis une nouvelle distance
                _rep3 = _rep2; _rep2 = _rep1; _rep1 = _rep0;
                longueur = LireLongueur(_longueur, posEtat);
                _etat = _etat < 7 ? 7 : 10;
                uint creneau = Arbre(_creneau, (int)Math.Min(longueur, 3) << 6, 6);
                if (creneau < 4) _rep0 = creneau;
                else
                {
                    int directs = (int)(creneau >> 1) - 1;
                    _rep0 = (2 | (creneau & 1)) << directs;
                    if (creneau < 14) _rep0 += ArbreInverse(_distances, (int)(_rep0 - creneau), directs);
                    else _rep0 += (Directs(directs - 4) << 4) + ArbreInverse(_alignement, 0, 4);
                }
            }
            longueur += 2;
            if (_rep0 >= _total || _rep0 >= (uint)_dico.Length || longueur > reste) throw Abime("copie hors de la fenêtre");
            reste -= (int)longueur;
            for (; longueur > 0; longueur--) Poser(Avant(_rep0));
        }
        if (_mp != _mFin) throw Abime("morceau mal terminé");
    }
}
