using PhotosWindows.Application.Abstractions;
using PhotosWindows.Application.People;
using PhotosWindows.Domain;

namespace PhotosWindows.Integration.Tests;

public sealed class PeopleUnitTests
{
    private static float[] V(params float[] values) => FaceClustering.Normalize(values);

    private static FaceVector F(long id, float[] v, long? person = null, FaceReview review = FaceReview.Suggested) => new(id, id * 10, person, review, v);

    [Fact]
    public void New_similar_faces_form_a_group_and_singletons_stay_ungrouped()
    {
        var plan = FaceClustering.Plan([F(1, V(1, 0, 0)), F(2, V(0.95f, 0.05f, 0)), F(3, V(0, 1, 0))]);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, a => Assert.Equal(1, a.NewGroupKey));
        Assert.DoesNotContain(plan, a => a.FaceId == 3);
    }

    [Fact]
    public void A_new_face_joins_the_most_similar_existing_group_above_the_threshold()
    {
        var plan = FaceClustering.Plan(
        [
            F(1, V(1, 0, 0), person: 7, review: FaceReview.Confirmed),
            F(2, V(0, 1, 0), person: 8),
            F(3, V(0.9f, 0.1f, 0)),
            F(4, V(0, 0, 1)),
        ]);
        var assignment = Assert.Single(plan);
        Assert.Equal(3, assignment.FaceId);
        Assert.Equal(7, assignment.PersonId);
    }

    [Fact]
    public void Below_the_threshold_nothing_is_joined()
    {
        var options = new ClusteringOptions(JoinExistingThreshold: 0.9f, NewGroupThreshold: 0.9f);
        var plan = FaceClustering.Plan([F(1, V(1, 0), person: 1), F(2, V(0.7f, 0.7f)), F(3, V(0.6f, 0.8f))], options);
        Assert.All(plan, a => Assert.Null(a.PersonId));
        Assert.Equal(2, plan.Count); // 2 and 3 are similar to each other (0.99)
    }

    [Fact]
    public void Confirmed_ignored_and_grouped_faces_are_never_moved()
    {
        var plan = FaceClustering.Plan(
        [
            F(1, V(1, 0), person: 1, review: FaceReview.Confirmed),
            F(2, V(1, 0), person: 2),
            F(3, V(1, 0), review: FaceReview.Ignored),
            F(4, V(1, 0), review: FaceReview.Confirmed),
        ]);
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_is_deterministic()
    {
        var rng = new Random(5);
        var faces = Enumerable.Range(1, 60).Select(i => F(i, V(Enumerable.Range(0, 8).Select(_ => (float)rng.NextDouble()).ToArray()))).ToList();
        var a = FaceClustering.Plan(faces);
        var b = FaceClustering.Plan(faces.AsEnumerable().Reverse().ToList());
        Assert.Equal(a.OrderBy(x => x.FaceId), b.OrderBy(x => x.FaceId));
    }

    [Fact]
    public void Ignored_faces_do_not_pull_new_faces_into_a_group()
    {
        var plan = FaceClustering.Plan([F(1, V(1, 0), person: 5, review: FaceReview.Ignored), F(2, V(1, 0))]);
        Assert.Empty(plan);
    }

    [Fact]
    public void Face_box_overlap_and_clamping()
    {
        var a = new FaceBox(0.1f, 0.1f, 0.2f, 0.2f);
        Assert.Equal(1f, a.IntersectionOverUnion(a), 3);
        Assert.Equal(0f, a.IntersectionOverUnion(new FaceBox(0.5f, 0.5f, 0.1f, 0.1f)));
        Assert.InRange(a.IntersectionOverUnion(new FaceBox(0.2f, 0.1f, 0.2f, 0.2f)), 0.33f, 0.34f);
        var clamped = new FaceBox(-0.1f, 0.9f, 0.3f, 0.3f).Clamp();
        Assert.Equal(new FaceBox(0f, 0.9f, 0.2f, 0.1f), clamped with { Width = MathF.Round(clamped.Width, 4), Height = MathF.Round(clamped.Height, 4) });
        Assert.True(clamped.IsValid);
    }

    [Fact]
    public void Square_crop_stays_inside_the_image()
    {
        var image = new RasterImage(100, 50, new byte[100 * 50 * 4]);
        var crop = PeopleService.CropSquare(image, new FaceBox(0.9f, 0.8f, 0.2f, 0.3f), 0.35f);
        Assert.Equal(crop.Width, crop.Height);
        Assert.InRange(crop.Width, 8, 50);
    }
}
