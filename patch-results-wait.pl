# Ranked Play results: wait only for the players who finished the map (a player who quit never sends a score, and
# waiting the full 10 s for it held the room's lock past the 5 s every other call on the room waits), and look every
# 250 ms instead of every second, so the lock is let go as soon as the scores are in.
undef $/;
my $s = <STDIN>;
my $old = q{                        if (retrievedScores.Length == State.Users.Count)
                            break;

                        await Task.Delay(1000, CancellationToken.None);};
my $new = q{                        // TomYum: only the players who finished the map will send a score (patch-results-wait.pl).
                        int finished = Room.Users.Count(u => State.Users.ContainsKey(u.UserID)
                                                             && (u.State == MultiplayerUserState.FinishedPlay || u.State == MultiplayerUserState.Results));
                        if (retrievedScores.Length >= State.Users.Count || retrievedScores.Length >= finished)
                            break;

                        await Task.Delay(250, CancellationToken.None);};
my $n = () = $s =~ /\Q$old\E/g;
die "ResultsStage wait loop not found exactly once ($n)\n" unless $n == 1;
$s =~ s/\Q$old\E/$new/;
print $s;
